// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Identity.Client;

namespace MinimalApi.Services;

/// <summary>
/// Implements the 'Read, Retrieve, Read' RAG Pattern
/// </summary>
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
            // Note the third parameter: DefaultAzureCredential() is used to authenticate the service.
            // It cycles through the available authentication methods in the following order:
            // 1. Environment variables
            // 2. Managed Identity
            // 3. Visual Studio
            // 4. Visual Studio Code
            // 5. Azure CLI
            // 6. Azure AD Integrated Authentication
            // 7. Interactive Browser Login
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
            //*************************************************
            // Prepare parameters for the search
            //*************************************************

            // Set the value of 'top' based on the value of 'overrides.Top' or default to 3
            var top = overrides?.Top ?? 3;

            // Semantic Captions: Verbatim sentences and phrases from a document that best summarize the content of the document.
            var useSemanticCaptions = overrides?.SemanticCaptions ?? false;

            // Semantic Ranking: Adds secondary ranking based on semantic similarity to the query.
            // Extracts the most relevant sentences from the top documents and ranks them based on their semantic similarity to the query.
            var useSemanticRanker = overrides?.SemanticRanker ?? false;

            // Set the value of 'excludeCategory' based on the value of 'overrides.ExcludeCategory' or default to null
            var excludeCategory = overrides?.ExcludeCategory ?? null;

            // Filter to exclude specific categories from the search results
            var filter = excludeCategory is null ? null : $"category ne '{excludeCategory}'";

            // Get the 'chat' service instance from the '_kernel' service
            IChatCompletion chat = _kernel.GetService<IChatCompletion>();

            // Get the 'embedding' service instance from the '_kernel' service
            ITextEmbeddingGeneration? embedding = _kernel.GetService<ITextEmbeddingGeneration>();

            // Initialize variable to cpature the embeddings of the user's prompt
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
            // step 1: Read
            // LLM reads user's query and calls LLM to refine
            // query for search proces
            // *************************************************

            // Initialize variable to capture the prompt
            string? query = null;

            // ** We'll perform refinement step for keyword, or non-semantic, searches
            // ** If Vector serach, then we want user's full sentence in order to understand
            // ** full semantics for the question and forge the keyword refinement step.
            if (overrides?.RetrievalMode != "Vector")
            {
                // Constructs a Kernel ChatQuery instance with the following prompt
                var getQueryChat = chat.CreateNewChat(@"You are a helpful AI assistant, generate search query for followup question.
Make your respond simple and precise. Return the query only, do not return any other text.
e.g.
Northwind Health Plus AND standard plan.
standard plan AND dental AND employee benefit.
");
                // Add the users current question to the 'getQueryChat' instance
                // ** Note how no history is added to the 'getQueryChat' instance **
                // Only the current question is added to the 'getQueryChat' instance
                getQueryChat.AddUserMessage(question);

                // Call to the Kernel, which calls OpenAI's 'GetChatCompletionsAsync' method
                // for a refined version of the user's question.
                var result = await chat.GetChatCompletionsAsync(
                    getQueryChat, // passes in prompt tempate which contain instuctions and the data
                    cancellationToken: cancellationToken);

                if (result.Count != 1)
                {
                    throw new InvalidOperationException("Failed to get search query");
                }

                // assign refined query to query variable
                query = result[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;
            }

            //*************************************************
            // step 2: Retrieve
            // use query to search relevant data or documents
            //*************************************************

            // Call to searchClient returns a list of relevant documents
            // Well pass the list of documents back to the calling client
            var documentContentList = await _searchClient.QueryDocumentsAsync(query, embeddings, overrides, cancellationToken);

            string documentContents = string.Empty;

            // Concatenate the title and content of each document in the 'documentContentList' array
            if (documentContentList.Length == 0)
            {
                documentContents = "no source available.";
            }
            else
            {
                documentContents = string.Join("\r", documentContentList.Select(x => $"{x.Title}:{x.Content}"));
            }

            // Print the 'documentContents' to the console for debugging purposes
            //Console.WriteLine(documentContents);

            //*************************************************
            // step 3: Read
            // LLM reads/analyzes retrieved content.
            // LLM formulates a comprehensive response.
            // The response is set back to the user.
            //*************************************************

            // Constructs a Kernel ChatQuery instance with the following prompt
            var answerChat = chat.CreateNewChat(
                "You are a system assistant who helps the company employees with their healthcare " +
                "plan questions, and questions about the employee handbook. Be brief in your answers");

            // add chat history
            foreach (var turn in history)
            {
                // add each user message to answerChat instance
                answerChat.AddUserMessage(turn.User);

                // C# Pattern Matching:
                // If the 'turn.Bot' is compatible with type botMessage
                // If so, then assign the value of 'turn.Bot' to the 'botMessage' variable
                // And, if true, then add the 'botMessage' to the 'answerChat' instance
                if (turn.Bot is { } botMessage)
                {
                    answerChat.AddAssistantMessage(botMessage);
                }
            }

            // Add a prompt template
            // Note how the 'documentContents' (from step 2) is embdded into the prompt
            // Note how it requests the answer in a json object format
            // Using Natural Language, it then asks the LLM to:
            //    Provide the answer (document) to the user question
            //    Describe how it determined the answer
            // Show the power of Prompt Engineering
            answerChat.AddUserMessage(@$" ## Source ##
{documentContents}
## End ##

You answer needs to be a json object with the following format.
{{
    ""answer"": // the answer to the question, add a source reference to the end of each sentence. e.g. Apple is a fruit [reference1.pdf][reference2.pdf]. If no source available, put the answer as I don't know.
    ""thoughts"": // brief thoughts on how you came up with the answer, e.g. what sources you used, what you thought about, etc.
}}");

            // Call the model using Semantic Kernel to execute the instructions from the prompt
            var answer = await chat.GetChatCompletionsAsync(
                           answerChat, // passes in prompt tempate which contain instuctions and the data
                           cancellationToken: cancellationToken);

            //** Extract the content portion of the Semantic Kernel response
            var answerJson = answer[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;

            //** Deserialize the json object
            var answerObject = JsonSerializer.Deserialize<JsonElement>(answerJson);

            //** Extract the answer portion of the Semantic Kernel response
            var ans = answerObject.GetProperty("answer").GetString() ?? throw new InvalidOperationException("Failed to get answer");
            
            //Console.WriteLine(answer);
            //var test = $"Searched for:<br>{query}<br>"; //<br>Prompt:<br>{prompt.Replace("\n", "<br>")}",

            //** Extract the thought portion of the Semantic Kernel response
            var thoughts = answerObject.GetProperty("thoughts").GetString() ?? throw new InvalidOperationException("Failed to get thoughts");

            //*************************************************
            // step 4
            // Put the LLM to work for value adds -- beyond the RAG pattern
            // This is cool and shows the power of LLMs 
            //*************************************************

            // ** Instruct model to generate follow-up questions
            if (overrides?.SuggestFollowupQuestions is true)
            {
                // Constructs a Kernel ChatQuery instance with the following prompt
                var followUpQuestionChat = chat.CreateNewChat(@"You are a helpful AI assistant");

                // Adds the prompt template -- embedding the answer portion of the previous response in step 3
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

                // Call the model using Semantic Kernel to execute the instructions from the prompt
                var followUpQuestions = await chat.GetChatCompletionsAsync(
                    followUpQuestionChat, // passes in prompt tempate which contain instuctions and the data
                    cancellationToken: cancellationToken);

                // Same pattern in step 3
                var followUpQuestionsJson = followUpQuestions[0].ModelResult.GetOpenAIChatResult().Choice.Message.Content;
                var followUpQuestionsObject = JsonSerializer.Deserialize<JsonElement>(followUpQuestionsJson);

                // Flatten the json object into a list of strings
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
