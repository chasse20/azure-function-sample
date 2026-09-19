using DocuSignModel = DocuSign.eSign.Model;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.DocuSign
{
	public interface IService
	{
		Task<DocuSignModel.Recipients> GetTemplateRecipientsAsync( string tTemplateId, CancellationToken tCancel );
		Task<DocuSignModel.EnvelopeSummary> CreateEnvelopeAsync( DocuSignModel.EnvelopeDefinition tEnvelopeDefinition, CancellationToken tCancel );
		Task<DocuSignModel.LockInformation> LockEnvelopeAsync( string tDocuSignEnvelopeId, string tLockedByApp, int tLockDurationInSeconds, CancellationToken tCancel );
		Task<DocuSignModel.EnvelopeUpdateSummary> VoidEnvelopeAsync( string tDocuSignEnvelopeId, string tReason, CancellationToken tCancel );
		Task<Stream> GetCombinedDocumentAsync( string tDocuSignEnvelopeId, CancellationToken tCancel );
		Task<DocuSignModel.FoldersResponse> MoveEnvelopeToRecycleBinAsync( string tDocuSignEnvelopeId, CancellationToken tCancel );
	}
}
