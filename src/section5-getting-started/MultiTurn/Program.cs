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
    name: "HistoryBuff",
    instructions: "You are a helpful History Teacher. Keep your answers concise and to the point.");

Console.WriteLine($"Agent '{supportAgent.Name}' is online.\n");

//4. Create the session (The Memory Container)
//This object will accumulate the conversation history and provide context to the agent for multi-turn conversations.

AgentSession Session = await supportAgent.CreateSessionAsync();

Console.WriteLine("History teacher online.\n");

//5. The conversation loop
while (true)
{
    Console.Write("User: ");
    string? userInput = Console.ReadLine();

    if(string.IsNullOrEmpty(userInput) || userInput.ToLower() == "exit") break;

    //We pass the 'session' into RunAsync.
    //The framework automatically appends the conversation history to the prompt, so the agent can respond in context.
    //sends the full history to the model, so it can respond in context.

    AgentResponse response = await supportAgent.RunAsync(userInput, Session);

    Console.WriteLine($"Agent: {response.Text}\n");

}

