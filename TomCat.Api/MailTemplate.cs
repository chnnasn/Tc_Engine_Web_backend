using System.Text.RegularExpressions;
namespace TomCat.Api;
public static class MailTemplate
{
    static readonly string Verification = Load("verification");
    static readonly string Recovery = Load("password-reset");
    static string Load(string name)
    {
        using var stream = typeof(MailTemplate).Assembly.GetManifestResourceStream($"TomCat.Api.MailTemplates.{name}.html")
            ?? throw new InvalidOperationException("Mail template missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public static string Render(string code, bool recovery)
    {
        if (!Regex.IsMatch(code, "^[0-9]{6}$")) throw new ArgumentException("Code must be six digits.", nameof(code));
        return (recovery ? Recovery : Verification).Replace("{{code}}", code[..3] + " " + code[3..]);
    }
}
