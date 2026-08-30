using EmailScraper.EmailProviders.Gmail;
using EmailScraper.EmailProviders.Microsoft365;

namespace EmailScraper.EmailProviders;

public static class EmailProviderFactory
{
    public static Task<IEmailProvider> CreateAsync(
        AppConfig appConfig,
        EmailSourceConfig source)
    {
        if (string.Equals(source.Provider, "Gmail", StringComparison.OrdinalIgnoreCase))
            return GmailEmailProvider.CreateAsync(appConfig, source);

        if (string.Equals(source.Provider, "Microsoft365", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source.Provider, "Office365", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source.Provider, "Outlook", StringComparison.OrdinalIgnoreCase))
            return Microsoft365EmailProvider.CreateAsync(appConfig, source);

        throw new InvalidOperationException(
            $"Email provider '{source.Provider}' is not supported. " +
            "Supported providers: Gmail, Microsoft365.");
    }
}
