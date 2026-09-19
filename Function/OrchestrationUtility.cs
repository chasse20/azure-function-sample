using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataService.Function
{
	public static class OrchestrationUtility
	{
		public static bool GetIsRunning( this OrchestrationMetadata tMetadata )
		{
			return tMetadata?.RuntimeStatus switch
			{
				OrchestrationRuntimeStatus.Pending => true,
				OrchestrationRuntimeStatus.Running => true,
				OrchestrationRuntimeStatus.Suspended => true,
				_ => false
			};
		}

		public static async Task<OrchestrationRuntimeStatus> StartAsync<TInput>( this DurableTaskClient tStarter, OrchestrationStart<TInput> tStart, CancellationToken tCancel )
		{
			tCancel.ThrowIfCancellationRequested();

			if ( tStart == null || string.IsNullOrWhiteSpace( tStart.Name ) || string.IsNullOrWhiteSpace( tStart.InstanceId ) )
			{
				throw new InvalidOperationException( "The orchestration name and instance ID are required." );
			}

			OrchestrationMetadata tempMetadata = await tStarter.GetInstanceAsync( tStart.InstanceId, tCancel );

			if ( tempMetadata != null )
			{
				return tempMetadata.RuntimeStatus;
			}

			await tStarter.ScheduleNewOrchestrationInstanceAsync( tStart.Name, tStart.Input, new StartOrchestrationOptions( tStart.InstanceId ), tCancel );

			return OrchestrationRuntimeStatus.Pending;
		}

		public static async Task PurgeTerminalOrchestrationAsync( this DurableTaskClient tStarter, string tInstanceId, OrchestrationMetadata tMetadata, CancellationToken tCancel )
		{
			if ( tMetadata == null )
			{
				return;
			}
			else if ( !tMetadata.IsCompleted )
			{
				throw new InvalidOperationException( $"Orchestration '{tInstanceId}' is not terminal." );
			}

			PurgeResult tempPurgeResult = await tStarter.PurgeInstanceAsync( tInstanceId, tCancel );

			if ( tempPurgeResult.PurgedInstanceCount != 1 )
			{
				throw new InvalidOperationException( $"Unable to purge orchestration '{tInstanceId}'." );
			}
		}
	}
}
