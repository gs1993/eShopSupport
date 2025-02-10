using System.Text;
using System.Threading.RateLimiting;
using eShopSupport.Backend.Data;
using eShopSupport.ServiceDefaults.Clients.Backend;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace eShopSupport.Backend.Services;

public class TicketClassifier(IServiceScopeFactory scopeFactory)
{
    // Because this LLM call can be triggered by external end-user actions, it's helpful to impose a rate limit
    // to prevent resource consumption abuse. If the rate limit is exceeded, we'll simply not compute updated summaries
    // for a while, but everything else will continue to work. In a real application, also consider:
    // - Adjusting the parameters based on your traffic and usage patterns
    // - Scoping the rate limit to be per-user
    private static TokenBucketRateLimiter RateLimiter = new(new()
    {
        // With these settings, we're limited to generating one summary every 2 seconds as a long-run average, but
        // can burst to up to 100 summaries in a short period if it's been several minutes since the last one.
        AutoReplenishment = true,
        TokenLimit = 100,
        ReplenishmentPeriod = TimeSpan.FromSeconds(10),
        TokensPerPeriod = 5,
    });

    public async Task<TicketType?> Classify(string ticketText, bool enforceRateLimit)
    {
        if (enforceRateLimit)
        {
            using var lease = RateLimiter.AttemptAcquire();
            return lease.IsAcquired
                ? await ClassifyTextAsync(ticketText)
                : null;
        }
        else
        {
            return await ClassifyTextAsync(ticketText);
        }
    }

    private async Task<TicketType?> ClassifyTextAsync(string ticketText)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TicketClassifier>>();

        var chatClient = scope.ServiceProvider.GetRequiredService<IChatClient>();
        string[] ticketTypes = Enum.GetNames(typeof(TicketType));

        var prompt = $$"""
            You are part of a customer support ticketing system.
            Your job is to assign Ticket type based on customer message. This is to help support agents
            understand the context quickly so they can help the customer efficiently.

            Here are details of a support ticket.

            Customer Message:

            {{ticketText}}

            Classification rules:

            1. Assign customer message to one of following types:
                 {{string.Join(", ", ticketTypes)}}, Unknown

            2. There should be ONLY ONE type assigned for customer message.

            3. If you cannot assign any ticket type set 'Unknown' ticket type.

            Respond as JSON in the following form: {
                "CustomerSatisfaction": "string"
            }
            """;

        var response = await chatClient.CompleteAsync<Response>(prompt);
        if (!response.TryGetResult(out var parsed))
            return null;

        return Enum.TryParse<TicketType>(parsed.TicketType, out var result) 
            ? result 
            : null;

    }

    private class Response
    {
        public string? TicketType { get; set; }
    }
}
