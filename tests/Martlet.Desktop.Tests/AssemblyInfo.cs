// WPF lazily initializes shared XAML metadata; independent STA window constructors can race.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
