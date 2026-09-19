namespace DataService.Function
{
	public sealed record OrchestrationStart<TInput>( string Name, string InstanceId, TInput Input );
}
