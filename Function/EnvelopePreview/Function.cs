using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DataService.Function.Envelope;
using DataService.SQL;
using DocuSignModel = DocuSign.eSign.Model;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.Function.EnvelopePreview
{
	public class Function( IDbContextFactory<Context> tDB, BlobContainerClient tBlob, DataService.DocuSign.IService tDocuSign, TaskOptions tTaskOptions, ILogger<Function> tLogger )
	{
		protected readonly IDbContextFactory<Context> _DB = tDB;
		protected readonly BlobContainerClient _blob = tBlob;
		public readonly DataService.DocuSign.IService _docuSign = tDocuSign;
		protected readonly ILogger<Function> _logger = tLogger;
		protected readonly TaskOptions _taskOptions = tTaskOptions;
		protected static string REQUIRED_ROLE = "DocumentWorkflow.Access";

		[Function( nameof( GetEnvelopePreviewAsync ) )]
		public async Task<HttpResponseData> GetEnvelopePreviewAsync( [HttpTrigger( AuthorizationLevel.Anonymous, "get", Route = "envelopePreview/{id}" )] HttpRequestData tRequest, int id, CancellationToken tCancel )
		{
			// Authenticate
			HttpResponseData tempResponse = AuthorizationUtility.GetAuthorizationResponse( tRequest, REQUIRED_ROLE );

			if ( tempResponse != null )
			{
				return tempResponse;
			}

			// Envelope
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.EnvelopePreview tempPreview = await tempContext.EnvelopePreview.AsNoTracking().Include( x => x.File ).FirstOrDefaultAsync( x => x.EnvelopeId == id, tCancel );

			if ( tempPreview == null )
			{
				return tRequest.CreateResponse( HttpStatusCode.NotFound );
			}

			tempResponse = tRequest.CreateResponse( HttpStatusCode.OK );
			await tempResponse.WriteAsJsonAsync( tempPreview, tCancel );
			return tempResponse;
		}

		[Function( nameof( PostEnvelopePreviewAsync ) )]
		public async Task<HttpResponseData> PostEnvelopePreviewAsync( [HttpTrigger( AuthorizationLevel.Anonymous, "post", Route = "envelopePreview/{id}" )] HttpRequestData tRequest, int id, [DurableClient] DurableTaskClient tStarter, CancellationToken tCancel )
		{
			// Authenticate
			HttpResponseData tempResponse = AuthorizationUtility.GetAuthorizationResponse( tRequest, REQUIRED_ROLE );

			if ( tempResponse != null )
			{
				return tempResponse;
			}

			// Envelope
			await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
			SQL.Envelope tempEnvelope = await tempContext.Envelope.Include( x => x.EnvelopePreview ).Include( x => x.EnvelopeFiles ).SingleOrDefaultAsync( x => x.EnvelopeId == id, tCancel );

			// Validate
			if ( tempEnvelope == null )
			{
				return tRequest.CreateResponse( HttpStatusCode.NotFound );
			}
			else if ( Envelope.Function.GetEnvelopeStatus( tempEnvelope ) != EnvelopeStatus.Draft )
			{
				return tRequest.CreateResponse( HttpStatusCode.Conflict );
			}
			else if ( tempEnvelope.EnvelopeFiles == null || tempEnvelope.EnvelopeFiles.Count == 0 )
			{
				return tRequest.CreateResponse( HttpStatusCode.BadRequest );
			}

			// Create new
			if ( tempEnvelope.EnvelopePreview == null )
			{
				tempEnvelope.EnvelopePreview = new() { EnvelopeId = tempEnvelope.EnvelopeId };

				await tempContext.EnvelopePreview.AddAsync( tempEnvelope.EnvelopePreview, tCancel );
				await tempContext.SaveChangesAsync( tCancel );
			}

			string tempInstanceId = $"EnvelopePreview{tempEnvelope.EnvelopeId}";
			OrchestrationMetadata tempMetadata = await tStarter.GetInstanceAsync( tempInstanceId, tCancel );

			if ( tempMetadata?.GetIsRunning() == true )
			{
				tempResponse = tRequest.CreateResponse( HttpStatusCode.Accepted );
				await tempResponse.WriteAsJsonAsync( tempEnvelope.EnvelopePreview, tCancel );

				return tempResponse;
			}
			else if ( tempMetadata != null )
			{
				if ( !tempMetadata.IsCompleted )
				{
					return tRequest.CreateResponse( HttpStatusCode.Conflict );
				}

				await tStarter.PurgeTerminalOrchestrationAsync( tempInstanceId, tempMetadata, tCancel );
			}

			await tStarter.ScheduleNewOrchestrationInstanceAsync
			(
				nameof( OrchestrateCreateEnvelopePreviewAsync ),
				new Input() { EnvelopeId = tempEnvelope.EnvelopeId, Date = DateTime.UtcNow },
				new StartOrchestrationOptions( tempInstanceId ),
				tCancel
			);

			HttpResponseData tempAccepted = tRequest.CreateResponse( HttpStatusCode.Accepted );
			await tempAccepted.WriteAsJsonAsync( tempEnvelope.EnvelopePreview, tCancel );
			return tempAccepted;
		}

		[Function( nameof( OrchestrateCreateEnvelopePreviewAsync ) )]
		public async Task OrchestrateCreateEnvelopePreviewAsync( [OrchestrationTrigger] TaskOrchestrationContext tContext )
		{
			Input tempInput = tContext.GetInput<Input>();
			string tempDocuSignEnvelopeId = null;
			int tempFileId = 0;

			try
			{
				tempDocuSignEnvelopeId = await tContext.CallActivityAsync<string>( nameof( CreateDocuSignEnvelopePreviewAsync ), tempInput, _taskOptions );

				if ( !string.IsNullOrWhiteSpace( tempDocuSignEnvelopeId ) )
				{
					tempFileId = await tContext.CallActivityAsync<int>( nameof( DownloadEnvelopePreviewAsync ), new DownloadInput() { EnvelopeId = tempInput.EnvelopeId, DocuSignEnvelopeId = tempDocuSignEnvelopeId }, _taskOptions );
				}
			}
			catch ( TaskFailedException ) { }
			finally
			{
				if ( !string.IsNullOrWhiteSpace( tempDocuSignEnvelopeId ) )
				{
					try
					{
						await tContext.CallActivityAsync( nameof( VoidEnvelopePreviewAsync ), tempDocuSignEnvelopeId, _taskOptions );
					}
					catch ( TaskFailedException )
					{
					}
				}

				await tContext.CallActivityAsync( nameof( UpdateEnvelopePreviewAsync ), new UpdateInput() { EnvelopeId = tempInput.EnvelopeId, FileId = tempFileId, DocuSignEnvelopeId = tempDocuSignEnvelopeId }, _taskOptions );
			}
		}

		[Function( nameof( CreateDocuSignEnvelopePreviewAsync ) )]
		public async Task<string> CreateDocuSignEnvelopePreviewAsync( [ActivityTrigger] Input tInput, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			try
			{
				await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
				SQL.Envelope tempEnvelope = await tempContext.Envelope.AsNoTracking().Include( x => x.EnvelopeFiles ).ThenInclude( x => x.File ).SingleOrDefaultAsync( x => x.EnvelopeId == tInput.EnvelopeId, tCancel );

				if ( tempEnvelope == null || Envelope.Function.GetEnvelopeStatus( tempEnvelope ) != EnvelopeStatus.Draft )
				{
					return null;
				}

				List<DocuSignModel.Document> tempDocuments = await Envelope.Function.GetDocumentsAsync( _blob, tempEnvelope.EnvelopeFiles, tCancel );
				DocuSignModel.EnvelopeDefinition tempDefinition = new()
				{
					EmailSubject = tempEnvelope.Name,
					Documents = tempDocuments,
					Status = "created"
				};

				DocuSignModel.EnvelopeSummary tempSummary = await _docuSign.CreateEnvelopeAsync( tempDefinition, tCancel );
				return tempSummary.EnvelopeId;
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( Exception tException )
			{
				_logger.LogError( tException, "Failed to create DocuSign preview for EnvelopeId {EnvelopeId}.", tInput.EnvelopeId );
				throw;
			}
		}

		[Function( nameof( DownloadEnvelopePreviewAsync ) )]
		public async Task<int> DownloadEnvelopePreviewAsync( [ActivityTrigger] DownloadInput tInput, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			try
			{
				await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
				var tempPreview = await tempContext.EnvelopePreview.AsNoTracking().Where( x => x.EnvelopeId == tInput.EnvelopeId ).Select
				(
					x => new
					{
						x.EnvelopeId,
						EnvelopeName = x.Envelope.Name
					}
				).SingleOrDefaultAsync( tCancel );

				if ( tempPreview == null )
				{
					_logger.LogError( "EnvelopePreview for EnvelopeId {EnvelopeId} was not found before its PDF download.", tInput.EnvelopeId );
					return 0;
				}

				// Upload Blob
				string tempBlobName = $"preview/{tInput.EnvelopeId}/{tInput.DocuSignEnvelopeId}/Preview{FileExtension.PDF}";
				BlobClient tempBlobClient = _blob.GetBlobClient( tempBlobName );
				Dictionary<string, string> tempMetaData = new()
				{
					{ "Name", tempPreview.EnvelopeName ?? string.Empty },
					{ "Envelope", tempPreview.EnvelopeId.ToString() }
				};

				DateTime tempCreatedDate;

				try
				{
					BlobProperties tempProperties = ( await tempBlobClient.GetPropertiesAsync( cancellationToken: tCancel ) ).Value;
					tempCreatedDate = tempProperties.LastModified.UtcDateTime;
				}
				catch ( Azure.RequestFailedException tException ) when ( tException.Status == (int)HttpStatusCode.NotFound )
				{
					await using Stream tempStream = await _docuSign.GetCombinedDocumentAsync( tInput.DocuSignEnvelopeId, tCancel );

					try
					{
						BlobFile tempBlobFile = await _blob.UploadFileBlobAsync( tempStream, tempBlobName, tempMetaData, tCancel );
						tempBlobClient = tempBlobFile.Client;
						tempCreatedDate = tempBlobFile.ContentInfo.LastModified.UtcDateTime;
					}
					catch ( Azure.RequestFailedException tConflictException ) when ( tConflictException.Status is (int)HttpStatusCode.Conflict or (int)HttpStatusCode.PreconditionFailed )
					{
						BlobProperties tempProperties = ( await tempBlobClient.GetPropertiesAsync( cancellationToken: tCancel ) ).Value;
						tempCreatedDate = tempProperties.LastModified.UtcDateTime;
					}
				}

				// Add File
				SQL.File tempFile = await tempContext.File.SingleOrDefaultAsync( x => x.AzureBlobURI == tempBlobName, tCancel );

				if ( tempFile == null )
				{
					tempFile = new()
					{
						CreatedDate = tempCreatedDate,
						AzureBlobURI = tempBlobName,
						Name = tempPreview.EnvelopeName,
						IsActive = true
					};

					await tempContext.File.AddAsync( tempFile, tCancel );
					await tempContext.SaveChangesAsync( tCancel );
				}

				tempMetaData[ "Id" ] = tempFile.FileId.ToString();

				try
				{
					await tempBlobClient.SetMetadataAsync( tempMetaData, cancellationToken: tCancel );
				}
				catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
				{
					throw;
				}
				catch ( Exception tException )
				{
					_logger.LogWarning( tException, "Preview FileId {FileId} was created, but its blob metadata was not updated.", tempFile.FileId );
				}

				return tempFile.FileId;
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( Exception tException )
			{
				_logger.LogError( tException, "Failed to download the preview PDF for EnvelopeId {EnvelopeId}.", tInput.EnvelopeId );
				throw;
			}
		}

		[Function( nameof( VoidEnvelopePreviewAsync ) )]
		public async Task VoidEnvelopePreviewAsync( [ActivityTrigger] string tDocuSignEnvelopeId, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			try
			{
				await _docuSign.MoveEnvelopeToRecycleBinAsync( tDocuSignEnvelopeId, tCancel );
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( Exception tException )
			{
				_logger.LogError( tException, "Failed to recycle DocuSign preview envelope {DocuSignEnvelopeId}.", tDocuSignEnvelopeId );
				throw;
			}
		}

		[Function( nameof( UpdateEnvelopePreviewAsync ) )]
		public async Task UpdateEnvelopePreviewAsync( [ActivityTrigger] UpdateInput tInput, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			try
			{
				await using Context tempContext = await _DB.CreateDbContextAsync( tCancel );
				SQL.EnvelopePreview tempPreview = await tempContext.EnvelopePreview.Include( x => x.File ).SingleOrDefaultAsync( x => x.EnvelopeId == tInput.EnvelopeId, tCancel );
				SQL.File tempFile = tInput.FileId > 0 ? await tempContext.File.FindAsync( [ tInput.FileId ], tCancel ) : null;
				string tempDeleteBlobName = null;

				if ( tempPreview != null && tempFile != null )
				{
					if ( tempPreview.FileId != tempFile.FileId )
					{
						SQL.File tempOldFile = tempPreview.File;

						tempPreview.File = tempFile;
						tempPreview.FileId = tempFile.FileId;
						tempPreview.DocusignEnvelopeId = tInput.DocuSignEnvelopeId;

						if ( tempOldFile != null )
						{
							tempDeleteBlobName = tempOldFile.AzureBlobURI;
							tempContext.File.Remove( tempOldFile );
						}
					}
				}
				else if ( tempPreview != null && !tempPreview.FileId.HasValue )
				{
					tempContext.EnvelopePreview.Remove( tempPreview );
				}
				else if ( tempPreview == null && tempFile != null )
				{
					tempDeleteBlobName = tempFile.AzureBlobURI;
					tempContext.File.Remove( tempFile );
				}

				if ( tempContext.ChangeTracker.HasChanges() )
				{
					await tempContext.SaveChangesAsync( tCancel );
				}

				if ( !string.IsNullOrWhiteSpace( tempDeleteBlobName ) )
				{
					await _blob.DeleteFileBlobAfterCommitAsync( tempDeleteBlobName, _logger, tCancel );
				}
			}
			catch ( OperationCanceledException ) when ( tCancel.IsCancellationRequested )
			{
				throw;
			}
			catch ( Exception tException )
			{
				_logger.LogError( tException, "Failed to update EnvelopePreview for EnvelopeId {EnvelopeId}.", tInput.EnvelopeId );
				throw;
			}
		}

		public static async Task<string> ProcessDeleteEnvelopePreviewAsync( Context tContext, SQL.EnvelopePreview tEnvelopePreview, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			if ( tEnvelopePreview != null )
			{
				string tempBlobName = null;

				if ( tEnvelopePreview.FileId.HasValue )
				{
					SQL.File tempFile = tEnvelopePreview.File ?? await tContext.File.FindAsync( [ tEnvelopePreview.FileId.Value ], tCancel );

					if ( tempFile != null )
					{
						tempBlobName = tempFile.AzureBlobURI;
						tContext.File.Remove( tempFile );
					}
				}

				tContext.EnvelopePreview.Remove( tEnvelopePreview );
				return tempBlobName;
			}

			return null;
		}
	}
}
