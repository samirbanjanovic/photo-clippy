// The CLI shares static System.CommandLine options and redirects process-wide consoles, so tests run serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
