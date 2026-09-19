using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.DurableTask;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SendGrid;
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Register Azure Functions
FunctionsApplicationBuilder tempBuilder = FunctionsApplication.CreateBuilder( args );
tempBuilder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults().UseAzureMonitorExporter();

// Azure Identity/Key Vault
DefaultAzureCredential tempAzureCredential = new();
SecretClient tempSecretClient = new( new Uri( GetRequiredConfigValue( tempBuilder.Configuration, "KEY_VAULT_URI" ) ), tempAzureCredential );

KeyVaultSecret tempSendGridKey = await tempSecretClient.GetSecretAsync( GetRequiredConfigValue( tempBuilder.Configuration, "SENDGRID_PRIVATE_KEY_NAME" ) );
KeyVaultSecret tempDocuSignPrivateKey = await tempSecretClient.GetSecretAsync( GetRequiredConfigValue( tempBuilder.Configuration, "DOCUSIGN_PRIVATE_KEY_NAME" ) );

// SendGrid
tempBuilder.Services.AddSingleton<ISendGridClient>( new SendGridClient( tempSendGridKey.Value ) );
tempBuilder.Services.AddSingleton<DataService.SendGrid.IService, DataService.SendGrid.Service>();

// DocuSign
tempBuilder.Services.AddSingleton<DataService.DocuSign.AuthenticationOptions>
(
	_ => new
	(
		GetRequiredConfigValue( tempBuilder.Configuration, "DOCUSIGN_CLIENT_URI" ),
		GetRequiredConfigValue( tempBuilder.Configuration, "DOCUSIGN_AUTH_SERVER" ),
		GetRequiredConfigValue( tempBuilder.Configuration, "DOCUSIGN_INTEGRATION_KEY" ),
		GetRequiredConfigValue( tempBuilder.Configuration, "DOCUSIGN_USER_ID" ),
		GetRequiredConfigValue( tempBuilder.Configuration, "DOCUSIGN_ACCOUNT_ID" ),
		Encoding.UTF8.GetBytes( tempDocuSignPrivateKey.Value ),
		GetRequiredIntConfigValue( tempBuilder.Configuration, "DOCUSIGN_TOKEN_EXPIRATION_HOURS" ),
		GetRequiredIntConfigValue( tempBuilder.Configuration, "DOCUSIGN_MAX_TOKEN_ATTEMPTS" )
	)
);

tempBuilder.Services.AddSingleton<DataService.DocuSign.IService, DataService.DocuSign.Service>();

// Blob
BlobServiceClient tempBlobClient = new( new Uri( GetRequiredConfigValue( tempBuilder.Configuration, "BLOB_URI" ) ), tempAzureCredential );
tempBuilder.Services.AddSingleton( tempBlobClient.GetBlobContainerClient( GetRequiredConfigValue( tempBuilder.Configuration, "BLOB_CONTAINER" ) ) );

// MSSQL
tempBuilder.Services.AddPooledDbContextFactory<DataService.SQL.ERP.Context>
(
	tOptions =>
	{
		tOptions.UseSqlServer( GetRequiredConfigValue( tempBuilder.Configuration, "SQL_CONNECTION" ), tOptions => tOptions.EnableRetryOnFailure() );
	}
);

// JSON
tempBuilder.Services.Configure<JsonSerializerOptions>
(
	tOptions =>
	{
		tOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
		tOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
	}
);

// Durable Functions Retry
tempBuilder.Services.AddSingleton
(
	TaskOptions.FromRetryPolicy
	(
		new
		(
			20,
			TimeSpan.FromMinutes( 1 ),
			2,
			TimeSpan.FromMinutes( 30 ),
			TimeSpan.FromHours( 6 )
		)
	)
);

// Build
await tempBuilder.Build().RunAsync();

// Helpers
static string GetRequiredConfigValue( IConfiguration tConfig, string tKey )
{
	string tempValue = tConfig[ tKey ];

	if ( string.IsNullOrWhiteSpace( tempValue ) )
	{
		throw new InvalidOperationException( $"{tKey} is not set." );
	}

	return tempValue;
}

static int GetRequiredIntConfigValue( IConfiguration tConfig, string tKey )
{
	string tempValue = GetRequiredConfigValue( tConfig, tKey );

	if ( !int.TryParse( tempValue, out int tempInt ) )
	{
		throw new InvalidOperationException( $"{tKey} must be an integer." );
	}

	return tempInt;
}
