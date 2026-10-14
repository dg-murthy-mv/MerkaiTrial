// =====================================================================
// Sent.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Emails/Sent.cshtml.cs
//
// NEW FILE (085). One email WE sent: who it went to, what happened to it
// (Queued → Sent → Delivered, or Bounced / Spam / Failed with the reason),
// the files attached, and — for a quote or invoice email — the email
// exactly as the customer received it. The twin of Emails/View (received).
//
// Opened from: the Email history card on a quote or invoice, the Emails
// tab on a deal / contact / lead, the bell notification "an email you sent
// couldn't be delivered", and the Email log in Settings → Notifications.
//
// ModuleName IS EMPTY, ON PURPOSE (like Emails/View). Whether this person
// may read THIS email depends on what it is about — quotes.read and the
// quote visible, invoices.read and the invoice visible, workspace admin
// for our own notification emails — and only the API knows that. So the
// page never calls ValidatePermissionAsync; the API answers "not found"
// for an email this person may not see, and the page turns that into a
// polite redirect. The API also returns CanRetry, which decides the "Send
// again" button; the API checks again when it is pressed.
//
// THE BODY is our own template, but it carries text a user typed (the
// personal message) and customer names, so it is shown the same way as a
// received email: <iframe sandbox srcdoc> with a CSP — no scripts, no
// forms, its own opaque origin. Notification emails to colleagues come
// back without a body (the email log's rule since 038).
// =====================================================================

using System.Text;
using MerkaiTrial.Admin.Web.Services.Emails;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Emails
{
    public class SentModel : AuthorizedPageModel
    {
        private readonly ISentEmailService _emails;

        public SentModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<SentModel> logger,
            ISentEmailService emails)
            : base(authorizationService, currentUserService, logger)
        {
            _emails = emails;
        }

        // See the header: no module, the API decides.
        protected override string ModuleName => string.Empty;

        public SentEmailDetailDto Email { get; private set; } = null!;
        public SentEmailListItemDto S => Email.Summary;

        /// <summary>"html" or "text".</summary>
        [BindProperty(SupportsGet = true)] public string? Tab { get; set; }

        /// <summary>Workspace admins get a link back to the email log.</summary>
        public bool IsWorkspaceAdmin { get; private set; }

        public bool HasHtml => !string.IsNullOrWhiteSpace(Email?.BodyHtml);
        public bool HasText => !string.IsNullOrWhiteSpace(Email?.BodyText);
        public bool HasBody => HasHtml || HasText;
        public bool ShowHtml => HasHtml && !string.Equals(Tab, "text", StringComparison.OrdinalIgnoreCase);

        // =============================================================
        // GET
        // =============================================================

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            try
            {
                Email = await _emails.GetAsync(id);
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That email could not be found, or you do not have access to it.";
                return RedirectToPage("/Dashboard/Index");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load sent email {Id}", id);
                TempData["ErrorMessage"] = "That email could not be loaded just now. Please try again.";
                return RedirectToPage("/Dashboard/Index");
            }

            try
            {
                var me = await CurrentUserService.GetCurrentUserAsync();
                IsWorkspaceAdmin = me?.IsTenantAdmin ?? false;
            }
            catch (Exception ex)
            {
                // Only decides a back-link; never worth failing the page.
                Logger.LogDebug(ex, "Could not resolve the current user for the email page");
            }

            return Page();
        }

        // =============================================================
        // POST — Send again. The API checks permission and state again.
        // =============================================================

        public async Task<IActionResult> OnPostRetryAsync(Guid id, string? returnUrl)
        {
            try
            {
                await _emails.RetryAsync(id);
                TempData["SuccessMessage"] = "The email is back in the queue and will be sent within a minute.";
            }
            catch (InvalidOperationException ex)
            {
                // The API's own sentence ("That email is already on its way.").
                TempData["ErrorMessage"] = ex.Message;
            }
            catch (KeyNotFoundException)
            {
                TempData["ErrorMessage"] = "That email could not be found, or you do not have access to it.";
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to retry sent email {Id}", id);
                TempData["ErrorMessage"] = "The email could not be put back in the queue. Please try again.";
            }

            // Back where the button was pressed — only ever a page on THIS site.
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return LocalRedirect(returnUrl);

            return RedirectToPage(new { id });
        }

        // =============================================================
        // view helpers
        // =============================================================

        /// <summary>The quote or invoice this email was about, or null.</summary>
        public string? DocumentUrl => S.EntityId is not Guid eid ? null : S.EntityType switch
        {
            "Quote"   => $"/Quotes/Detail?id={eid}",
            "Invoice" => $"/Invoices/Detail?id={eid}",
            _         => null
        };

        public string DocumentIcon => S.EntityType switch
        {
            "Quote"   => "bi-file-earmark-text",
            "Invoice" => "bi-receipt",
            _         => "bi-link-45deg"
        };

        /// <summary>
        /// A customer email always comes back WITH its body; a notification
        /// to a colleague never does (see the header) — so the body is the
        /// honest test, not the event type, which both kinds share.
        /// </summary>
        public string What => !HasBody
            ? "Notification email to a colleague"
            : Email.EventType switch
            {
                "QuoteSent"   => "Quote email to the customer",
                "InvoiceSent" => "Invoice email to the customer",
                _             => "Email to the customer"
            };

        /// <summary>One step of the delivery timeline.</summary>
        public sealed record Step(string Label, DateTime? AtUtc, string Icon, string Tone, string? Note);

        /// <summary>
        /// What happened, in order. Only steps that happened (or are the
        /// one being waited on) — a timeline full of grey "not yet" rows
        /// reads as broken.
        /// </summary>
        public IReadOnlyList<Step> Timeline
        {
            get
            {
                var steps = new List<Step>
                {
                    new("Queued", S.CreatedAtUtc, "bi-inbox", "text-secondary",
                        S.SentByName is null ? null : $"by {S.SentByName}")
                };

                switch (S.State)
                {
                    case "Queued":
                    case "Sending":
                        steps.Add(new("Sending", null, "bi-hourglass-split", "text-info", S.StateHint));
                        break;

                    case "Retrying":
                        steps.Add(new($"Attempt {S.AttemptCount} failed", null, "bi-clock-history", "text-warning", S.StateHint));
                        break;

                    case "Failed":
                        steps.Add(new($"Gave up after {S.AttemptCount} attempt{(S.AttemptCount == 1 ? "" : "s")}", null,
                            "bi-x-circle", "text-danger", S.LastError));
                        break;

                    case "Cancelled":
                        steps.Add(new("Cancelled", null, "bi-dash-circle", "text-secondary", "Cancelled before it was sent."));
                        break;

                    default:
                        // It left: Sent, then whatever the recipient's server did.
                        steps.Add(new("Sent", S.SentAtUtc, "bi-check2", "text-primary", "Accepted by the email provider."));

                        steps.Add(S.State switch
                        {
                            "Delivered"      => new Step("Delivered", S.DeliveryStatusAtUtc, "bi-check2-all", "text-success", S.StateHint),
                            "Delayed"        => new Step("Delayed", S.DeliveryStatusAtUtc, "bi-clock-history", "text-warning", S.StateHint),
                            "Bounced"        => new Step("Bounced", S.DeliveryStatusAtUtc, "bi-envelope-x", "text-danger", S.StateHint),
                            "Spam"           => new Step("Marked as spam", S.DeliveryStatusAtUtc, "bi-exclamation-octagon", "text-danger", S.StateHint),
                            "DeliveryFailed" => new Step("Not delivered", S.DeliveryStatusAtUtc, "bi-envelope-x", "text-danger", S.StateHint),
                            _                => new Step("Waiting for delivery", null, "bi-hourglass", "text-muted",
                                                         "Usually within a minute. If this stays here, the delivery events may not be reaching Merkai — see SETUP for round 085.")
                        });
                        break;
                }

                return steps;
            }
        }

        /// <summary>What to do about a problem email, in a sentence — or null.</summary>
        public string? WhatToDo => S.State switch
        {
            "Bounced" or "DeliveryFailed" =>
                $"Check the address ({S.ToAddress}) with the customer, correct it on the contact, then send the {DocumentWord} again.",
            "Spam" =>
                "The customer's mail system put it in spam. Ask them to look in their spam folder and mark it as not spam; future emails will then reach the inbox.",
            "Failed" when Email.CanRetry =>
                "Nothing was delivered. Press \"Send again\" to retry — the same email, to the same address.",
            "Failed" =>
                "Nothing was delivered. Someone who can send this " + DocumentWord + " can try again.",
            "Delayed" =>
                "Nothing to do yet — the provider keeps trying for a while. If it finally fails, this page will say so.",
            _ => null
        };

        private string DocumentWord => S.EntityType switch
        {
            "Quote"   => "quote",
            "Invoice" => "invoice",
            _         => "email"
        };

        public static string FileSize(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB"
          : bytes >= 1024        ? $"{bytes / 1024d:0} KB"
          :                        $"{bytes} B";

        /// <summary>
        /// The document the sandboxed frame shows. Our own template, but
        /// still behind a CSP that allows no scripts and no remote content
        /// other than pictures.
        /// </summary>
        public string SrcDoc
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
                sb.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; ");
                sb.Append("img-src data: https:; style-src 'unsafe-inline'; font-src data:\">");
                sb.Append("<base target=\"_blank\">");
                sb.Append("<style>body{font-family:Inter,system-ui,-apple-system,'Segoe UI',Arial,sans-serif;");
                sb.Append("font-size:14px;line-height:1.5;color:#1e293b;margin:0;word-wrap:break-word;}");
                sb.Append("img{max-width:100%;height:auto;}table{max-width:100%;}</style>");
                sb.Append("</head><body>");
                sb.Append(Email?.BodyHtml ?? string.Empty);
                sb.Append("</body></html>");
                return sb.ToString();
            }
        }
    }
}
