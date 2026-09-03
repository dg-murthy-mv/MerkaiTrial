using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace MerkaiTrial.WebApi.Services;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromName { get; set; } = "MerkaiTrial";
}

public sealed class SmtpEmailSender(IOptions<SmtpOptions> opt) : IEmailSender
{
    private readonly SmtpOptions _o = opt.Value;

    public async Task SendAsync(string toEmail, string subject, string htmlBody)
    {
        using var msg = new MailMessage
        {
            From = new MailAddress(_o.User, _o.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        msg.To.Add(toEmail);

        using var smtp = new SmtpClient(_o.Host, _o.Port)
        {
            Credentials = new NetworkCredential(_o.User, _o.Password),
            EnableSsl = true
        };
        await smtp.SendMailAsync(msg);
    }
}
