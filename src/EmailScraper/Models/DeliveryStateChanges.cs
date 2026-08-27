namespace EmailScraper.Models;

public sealed record DeliveryStateChanges(
    int CurrentlyUnsent,
    int NewlyUnsent,
    int NewlyDelivered);
