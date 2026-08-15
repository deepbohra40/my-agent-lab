using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;

// Follow-along scratchpad for Section 5 lecture 22 — MinimalAgent + DevUI.
//
// This is the only file in MinimalAgent worth typing. AppHost and ServiceDefaults
// are Aspire boilerplate and were copied verbatim.
//
// Two shifts from the console apps you've built so far:
//   - the agent is registered in DI, not held in a local variable
//   - the IChatClient gets an OpenTelemetry middleware layer so the Aspire
//     dashboard can render what it does
//
// Runs green as-is (health endpoints only) — fill in steps 2 onwards.
// Set AppHost as the startup project, not this one.

var builder = WebApplication.CreateBuilder(args);

// Aspire: OpenTelemetry, health checks, service discovery, HTTP resilience.
builder.AddServiceDefaults();

// 1. Define the variables we extracted from Microsoft Foundry
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? "gpt-5-mini";

// 2. Instantiate the chat client — this time with the OpenTelemetry middleware.
//    .AsBuilder() opens the Microsoft.Extensions.AI pipeline; .UseOpenTelemetry()
//    emits GenAI spans that ServiceDefaults exports to the dashboard.
//    EnableSensitiveData puts prompts and completions in the trace — dev only.
//
IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
    .GetChatClient(deployment)
    .AsIChatClient()
    .AsBuilder()
    .UseOpenTelemetry(configure: c => c.EnableSensitiveData = true)
    .Build();
builder.Services.AddSingleton(chatClient);

// 3. Register the agent in DI by name, rather than newing it up locally.
//    AddAIAgent registers it as a *keyed* service — the name is the key.
//
builder.AddAIAgent(
    name: "NetworkSupportAgent",
    instructions: """
         You are a Tier 1 IT Support Agent.
         Your answers must be concise, professional, and limited strictly to troubleshooting network and VPN connectivity.        
         Keep responses concise — 3-5 sentences per turn. Be direct and opinionated.
         """,
    chatClient);

// 4. DevUI — a MAF package, not Aspire. Gives a chat playground at /devui.
//    The OpenAI Responses/Conversations endpoints are what DevUI talks to.
//
builder.AddDevUI();
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

var app = builder.Build();

// Aspire's /health and /alive endpoints.
app.MapDefaultEndpoints();

// 5. Map the DevUI + OpenAI-compatible endpoints.

app.MapDevUI();
app.MapOpenAIResponses();
app.MapOpenAIConversations();

// 6. A plain HTTP endpoint that drives the agent.
//    [FromKeyedServices] is how you pull a named agent out of DI — this is the
//    part that differs from every console app so far.
//
app.MapPost("/api/chat", async (ChatRequest request,
    [FromKeyedServices("NetworkSupportAgent")] AIAgent agent) =>
{
    var response = await agent.RunAsync(request.Message);
    return Results.Ok(new { response = response.Text });
});

app.MapGet("/", () => "Nothing wired up yet — fill in steps 2-6.");

app.Run();

// Type declarations go after top-level statements.
 record ChatRequest(string Message);
