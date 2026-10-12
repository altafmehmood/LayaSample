using Microsoft.Extensions.Options;

namespace DocIntake.ExtractionService;

/// <summary>One tier of a model's list price, in USD per million tokens. A null limit is the open-ended top tier.</summary>
public sealed class PriceTier
{
    /// <summary>The tier applies to calls with at most this many input tokens.</summary>
    public long? MaxInputTokens { get; set; }
    public decimal InputPerMillion { get; set; }
    public decimal CachedInputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

public sealed class ModelPriceOptions
{
    public List<PriceTier> Tiers { get; set; } = [];
}

/// <summary>List prices from the <c>Extraction:Pricing</c> section, kept with the date they were read.</summary>
public sealed class PricingOptions
{
    public const string Section = "Extraction:Pricing";

    public string AsOf { get; set; } = "";
    public Dictionary<string, ModelPriceOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record PricedUsage(decimal CostUsd, string PriceKey);

/// <summary>Turns token counts into a list-price cost. Not the invoice: batch, flex and discounts are not modelled.</summary>
public sealed class ModelPricing(IOptions<PricingOptions> options)
{
    private readonly PricingOptions _options = options.Value;

    /// <summary>Null when the model has no configured price.</summary>
    public PricedUsage? Price(string model, long inputTokens, long cachedInputTokens, long outputTokens)
    {
        if (!_options.Models.TryGetValue(model, out var prices) || prices.Tiers.Count == 0)
            return null;

        // The cheapest tier that still covers this call's input; beyond the last limit, the last tier applies.
        var tiers = prices.Tiers.OrderBy(t => t.MaxInputTokens ?? long.MaxValue).ToList();
        var tier = tiers.FirstOrDefault(t => inputTokens <= (t.MaxInputTokens ?? long.MaxValue)) ?? tiers[^1];

        // Cached tokens are part of the input count, billed at their own rate instead of the full one.
        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var cost = ((inputTokens - cached) * tier.InputPerMillion + cached * tier.CachedInputPerMillion + outputTokens * tier.OutputPerMillion) / 1_000_000m;
        return new PricedUsage(Math.Round(cost, 6, MidpointRounding.AwayFromZero), $"{model}@{_options.AsOf}");
    }
}
