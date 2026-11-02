using Xunit;

// Disable parallelization across tests to prevent static state race conditions (RealtimeTickCollector, AuthService, etc.)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
