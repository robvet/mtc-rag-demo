// Copyright (c) Microsoft. All rights reserved.

namespace MinimalApi.Extensions;

internal static class KeyVaultConfigurationBuilderExtensions
{
    internal static IConfigurationBuilder ConfigureAzureKeyVault(this IConfigurationBuilder builder, string azureKeyVaultEndpoint)
    {

        //var azureKeyVaultEndpoint = Environment.GetEnvironmentVariable("AZURE_KEY_VAULT_ENDPOINT");
        
        ArgumentNullException.ThrowIfNullOrEmpty(azureKeyVaultEndpoint);

        builder.AddAzureKeyVault(
            new Uri(azureKeyVaultEndpoint), new DefaultAzureCredential());

        return builder;
    }
}

//var builder = new ConfigurationBuilder()
//    .SetBasePath(AppContext.BaseDirectory)
//    .AddUserSecrets<Startup>();

//var configuration = builder.Build();

//// Set environment variables based on values in the usersecrets.json file
//foreach (var secret in configuration.AsEnumerable())
//{
//    if (!string.IsNullOrEmpty(secret.Value))
//    {
//        Environment.SetEnvironmentVariable(secret.Key, secret.Value);
//    }
//}
