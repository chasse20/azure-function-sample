using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace DataService.SendGrid
{
	public interface IService
	{
		Task SendEmailAsync( string tSender, string tReceiver, string tTemplateId, Dictionary<string, object> tTemplateData, CancellationToken tCancel );
	}
}
