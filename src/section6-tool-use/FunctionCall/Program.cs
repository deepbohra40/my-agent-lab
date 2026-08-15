using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using System.ComponentModel;

// Follow-along scratchpad for Section 6 — function calling.
//
// Note the file shape: an explicit Program.Main, not top-level statements like
// section 5. That is deliberate — the instructor declares a public static tool
// class in this file, and his snippets paste in cleanly only in this shape.
//
// Runs green as-is (prints the banner and exits) — fill in steps 1 onwards.

// 1. Define the tool.
//    Two things carry all the weight here, and neither is C#:
//      - [Description] on the METHOD tells the model when to call it
//      - [Description] on each PARAMETER tells it what to pass
//    These strings are shipped to the model as the tool schema. They are prompt
//    text, not documentation — vague wording here is a bug, not a style nit.
//
// public static class LogisticsTools
// {
//     [Description("...")]
//     public static string GetOrderStatus(
//         [Description("...")] string orderId)
//     {
//         // A deterministic stand-in for a real database or API call.
//     }
// }

// 1. Define the Enterprise Tool
public static class LogisticsTools
{
    [Description("Retrieves the current shipping status of an enterprise logistics order. Invoke this tool ONLY when the user explicitly provides an Order ID.")]
    public static string GetOrderStatus(
        [Description("The exact, case-sensitive alphanumeric order identifier. Format must be 'ORD-' followed by 5 digits (e.g., ORD-12345).")] string orderId)
    {
        // Simulating a deterministic database or external API call
        if (orderId == "ORD-12345") return "IN TRANSIT - Estimated Delivery Tomorrow";
        if (orderId == "ORD-99999") return "PENDING - Awaiting Stock Validation";
        return "UNKNOWN - Order ID not found in the logistics system.";
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        // 2. Define the variables we extracted from Microsoft Foundry
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
            ?? "gpt-5-mini";

        Console.WriteLine($"endpoint   {endpoint}");
        Console.WriteLine($"deployment {deployment}\n");

        // 3. Build the agent and equip the tool.
        //    AIFunctionFactory.Create reflects over the method and generates the
        //    JSON schema the model sees. The tools: parameter is the new bit.
        //
        AIAgent agent = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
           .GetChatClient(deployment)
           .AsAIAgent(
               name: "LogisticsSupport",
               instructions: "You are a customer support agent. Help users track their orders concisely.",
               // We dynamically generate the AITool and pass it into the agent's capabilities
               tools: [AIFunctionFactory.Create(LogisticsTools.GetOrderStatus)]
           );

        // 4. Synchronous execution — RunAsync.
        //    Watch what the framework does for you: the model replies with a
        //    tool-call request, MAF invokes your C# method, feeds the result
        //    back, and the model answers. One await, several round trips.
        //    That also means one RunAsync here costs more than in section 5.

        // 5. Streaming execution — RunStreamingAsync.
        //    Same tool loop, tokens arriving as they are produced.

        Console.WriteLine("Nothing wired up yet — fill in steps 1, 3-5.");

        await Task.CompletedTask;
    }
}
