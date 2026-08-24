using EmailScraper.EmailProviders.Gmail;

namespace EmailScraper.EmailProviders;

public static class EmailProviderFactory
{
    public static Task<IEmailProvider> CreateAsync(AppConfig config)
    {
        if (string.Equals(config.EmailProvider, "Gmail", StringComparison.OrdinalIgnoreCase))
            return GmailEmailProvider.CreateAsync(config);

        throw new InvalidOperationException(
            $"Email provider '{config.EmailProvider}' is not supported. Supported providers: Gmail.");
    }
}
