// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Identity.Client;

namespace MinimalApi.Services;

public class ReadRetrieveReadChatService
{
    private readonly SearchClient _searchClient;
    private readonly IKernel _kernel;
    private readonly IConfiguration _configuration;

      public ReadRetrieveReadChatService(
        SearchClient searchClient, // Represents the search client used in the service
        OpenAIClient client, // Represents the OpenAI client used in the service
        IConfiguration configuration) // Represents the configuration used in the service
    {
        _searchClient = searchClient;

        // Fetch the name of the deployed model from the configuration (gpt-35-turbo)
        var deployedModelName = configuration["AzureOpenAiChatGptDeployment"];
        ArgumentNullException.ThrowIfNullOrWhiteSpace(deployedModelName);

        // Build the kernel with the Azure chat completion service
        var kernelBuilder = Kernel.Builder.WithAzureChatCompletionService(deployedModelName, client);

        // Fetch the name of the deployed embedding model from the configuration (text-embedding-ada-002)
        var embeddingModelName = configuration["AzureOpenAiEmbeddingDeployment"];

        if (!string.IsNullOrEmpty(embeddingModelName))
        {
            // Fetch the endpoint of the deployed embedding model from the configuration
            var endpoint = configuration["AzureOpenAiServiceEndpoint"];
            ArgumentNullException.ThrowIfNullOrWhiteSpace(endpoint);

            // Add Azure text embedding generation service to the kernel instance
            kernelBuilder = kernelBuilder.WithAzureTextEmbeddingGenerationService(embeddingModelName, endpoint, new DefaultAzureCredential());
        }
        _kernel = kernelBuilder.Build();
        _configuration = configuration;
    }

    public async Task<ApproachResponse> ReplyAsync(
        ChatTurn[] history,
        RequestOverrides? overrides,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Set the value of 'top' based on the value of 'overrides.Top' or default to 3
            var top = overrides?.Top ?? 3;

            // Set the value of 'useSemanticCaptions' based on the value of 'overrides.SemanticCaptions' or default to false
            var useSemanticCaptions = overrides?.SemanticCaptions ?? false;

            // Set the value of 'useSemanticRanker' based on the value of 'overrides.SemanticRanker' or default to false
            var useSemanticRanker = overrides?.SemanticRanker ?? false;

            // Set the value of 'excludeCategory' based on the value of 'overrides.ExcludeCategory' or default to null
            var excludeCategory = overrides?.ExcludeCategory ?? null;

            // Set the value of 'filter' based on the value of 'excludeCategory'
            // If 'excludeCategory' is null, 'filter' should be null, otherwise it should be a string with the appropriate condition
            var filter = excludeCategory is null ? null : $"category ne '{excludeCategory}'";

            // Get the 'chat' service instance from the '_kernel' service
            IChatCompletion chat = _kernel.GetService<IChatCompletion>();

            // Get the 'embedding' service instance from the '_kernel' service
            ITextEmbeddingGeneration? embedding = _kernel.GetService<ITextEmbeddingGeneration>();

            // Initialize 'embeddings' as null
            float[]? embeddings = null;

            // Get the last user question from the 'history' array and store it in the 'question' variable
            var question = history.LastOrDefault()?.User is { } userQuestion
                ? userQuestion
                : throw new InvalidOperationException("Use question is null");

            // Check if 'overrides.RetrievalMode' is not "Text" and 'embedding' is not null
            if (overrides?.RetrievalMode != "Text" && embedding is not null)
            {
                // Generate embeddings for the 'question' using the 'embedding' service and convert them to an array
                embeddings = (await embedding.GenerateEmbeddingAsync(question, cancellationToken: cancellationToken)).ToArray();
            }

            //*************************************************
            // step 1
            // use llm to get query if retrieval mode is not vector
            //*************************************************
            string? query = null;
            if (overrides?.RetrievalMode != "Vector")
            {
                var getQueryChat = chat.CreateNewChat(@"You are a helpful AI assistant, generate search query for followup question.
Make your respond simple and precise. Return the query only, do not return any other text.
e.g.
Northwind Health Plus AND standard plan.
standard plan AND dental AND employee benefit.
");

                getQueryChat.AddUserMessage(question);
                var result = await chat.GetChatCompletionsAsync(
                    getQueryChat,
                    cancellationToken: cancellationToken);

                if (result.Count != 1)
                {
                    throw new InvalidOperationException("Failed to get search query");
                }

                query = result[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;
            }

            //*************************************************
            // step 2
            // use query to search related docs
            //*******************************************
            var documentContentList = await _searchClient.QueryDocumentsAsync(query, embeddings, overrides, cancellationToken);

            string documentContents = string.Empty;
            if (documentContentList.Length == 0)
            {
                documentContents = "no source available.";
            }
            else
            {
                documentContents = string.Join("\r", documentContentList.Select(x => $"{x.Title}:{x.Content}"));
            }

            Console.WriteLine(documentContents);
            // step 3
            // put together related docs and conversation history to generate answer
            var answerChat = chat.CreateNewChat(
                "You are a system assistant who helps the company employees with their healthcare " +
                "plan questions, and questions about the employee handbook. Be brief in your answers");

            // add chat history
            foreach (var turn in history)
            {
                answerChat.AddUserMessage(turn.User);
                if (turn.Bot is { } botMessage)
                {
                    answerChat.AddAssistantMessage(botMessage);
                }
            }

            // format prompt
            answerChat.AddUserMessage(@$" ## Source ##
{documentContents}
## End ##

You answer needs to be a json object with the following format.
{{
    ""answer"": // the answer to the question, add a source reference to the end of each sentence. e.g. Apple is a fruit [reference1.pdf][reference2.pdf]. If no source available, put the answer as I don't know.
    ""thoughts"": // brief thoughts on how you came up with the answer, e.g. what sources you used, what you thought about, etc.
}}");

            // get answer
            var answer = await chat.GetChatCompletionsAsync(
                           answerChat,
                           cancellationToken: cancellationToken);
            var answerJson = answer[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;
            var answerObject = JsonSerializer.Deserialize<JsonElement>(answerJson);
            var ans = answerObject.GetProperty("answer").GetString() ?? throw new InvalidOperationException("Failed to get answer");
            //var thoughts = answerObject.GetProperty("thoughts").GetString() ?? throw new InvalidOperationException("Failed to get thoughts");

            var test = $"Searched for:<br>{query}<br>"; //<br>Prompt:<br>{prompt.Replace("\n", "<br>")}",

            var thoughts = answerObject.GetProperty("thoughts").GetString() ?? throw new InvalidOperationException("Failed to get thoughts");

            // step 4
            // add follow up questions if requested
            if (overrides?.SuggestFollowupQuestions is true)
            {
                var followUpQuestionChat = chat.CreateNewChat(@"You are a helpful AI assistant");
                followUpQuestionChat.AddUserMessage($@"Generate three follow-up question based on the answer you just generated.
# Answer
{ans}

# Format of the response
Return the follow-up question as a json string list.
e.g.
[
    ""What is the deductible?"",
    ""What is the co-pay?"",
    ""What is the out-of-pocket maximum?""
]");

                var followUpQuestions = await chat.GetChatCompletionsAsync(
                    followUpQuestionChat,
                    cancellationToken: cancellationToken);

                var followUpQuestionsJson = followUpQuestions[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;
                var followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestionsJson);
                var followUpQuestionsList = followUpQuestionsObject.EnumerateArray().Select(x => x.GetString()).ToList();
                foreach (var followUpQuestion in followUpQuestionsList)
                {
                    ans += $" <<{followUpQuestion}>> ";
                }
            }
            return new ApproachResponse(
                DataPoints: documentContentList,
                Answer: ans,
                Thoughts: thoughts,
                CitationBaseUrl: _configuration.ToCitationBaseUrl());
        }
        catch (Exception ex)
        {
            var error = ex.ToString();
            throw;
        }
    }
}
