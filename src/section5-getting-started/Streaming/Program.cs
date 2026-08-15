using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Follow-along scratchpad for Section 5. Type the instructor's code into the
// numbered gaps below; the config block is pure friction, so it's already here.
//
// Runs green as-is (prints the banner and exits) — fill in step 2 onwards.

// 1. Define the variables we extracted from Microsoft Foundry
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? "gpt-5-mini";

// 2. Instantiate the universal chat client with OpenTelemetry GenAI instrumentation
IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

//3. Define agent anatomy

AIAgent supportAgent = chatClient.AsAIAgent(
    name: "NetworkSupport",
    instructions: "You are a helpful network support agent. Keep your answers concise and to the point.");

Console.WriteLine($"Agent '{supportAgent.Name}' is online.\n");

//4. Excecute the agent

string userIssue = "I am getting a DNS resolution error when connecting to the corporate VPN from a cofee shop.";
Console.WriteLine($"User: '{userIssue}'\n");

////Non-streaming execution
//AgentResponse agentResponse = await supportAgent.RunAsync(userIssue);
//Console.WriteLine($"Agent: '{agentResponse.Text}'\n");

//streaming execution

await foreach (AgentResponseUpdate update in supportAgent.RunStreamingAsync(userIssue))
{    
        Console.Write(update.Text);    
}
