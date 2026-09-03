namespace MerkaiTrial.WebApi.Services;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody);
}
