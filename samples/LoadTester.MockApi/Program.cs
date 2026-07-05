using System.Collections.Concurrent;

// Tiny in-memory payments API used to try out the load tester without touching a real system.
// Latency is randomized; add ?delayMs=N to any endpoint to force a specific delay.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

var payments = new ConcurrentDictionary<string, object>();

async Task Delay(int? delayMs, int min = 5, int max = 60) =>
    await Task.Delay(delayMs ?? Random.Shared.Next(min, max));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/payments", async (int? page, int? delayMs) =>
{
    await Delay(delayMs);
    return Results.Ok(new
    {
        page = page ?? 1,
        items = Enumerable.Range(1, 20).Select(i => new
        {
            id = Guid.NewGuid().ToString(),
            amount = Math.Round(Random.Shared.NextDouble() * 500, 2),
            currency = "GBP",
            status = "Settled",
        }),
    });
});

app.MapGet("/api/payments/{id}", async (string id, int? delayMs) =>
{
    await Delay(delayMs);
    return payments.TryGetValue(id, out var payment)
        ? Results.Ok(payment)
        : Results.NotFound(new { error = "payment not found", id });
});

app.MapPost("/api/payments", async (PaymentRequest request, int? delayMs) =>
{
    await Delay(delayMs, 15, 120);
    var id = Guid.NewGuid().ToString();
    var payment = new
    {
        id,
        status = "Authorized",
        storeId = request.StoreId,
        cardId = request.CardId,
        amount = request.Transactions?.Sum(t => t.Amount) ?? 0,
        createdUtc = DateTime.UtcNow,
    };
    payments[id] = payment;
    return Results.Created($"/api/payments/{id}", payment);
});

// Simulates the degraded endpoint: slow and occasionally failing under any load.
app.MapGet("/api/slow", async (int? delayMs) =>
{
    await Delay(delayMs, 800, 2500);
    return Random.Shared.NextDouble() < 0.1
        ? Results.StatusCode(StatusCodes.Status500InternalServerError)
        : Results.Ok(new { status = "eventually" });
});

app.Run("http://localhost:5089");

record TransactionLine(decimal Amount, string Currency, string? TransactionType);

record PaymentRequest(
    string StoreId,
    string CardId,
    string? UserName,
    string? CreatedBy,
    List<TransactionLine>? Transactions,
    string? ApplicationBatchId);
