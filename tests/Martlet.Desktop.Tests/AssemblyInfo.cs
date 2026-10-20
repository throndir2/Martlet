// WPF's XAML resource loading (Application.LoadComponent over the assembly's resource package) is not safe on several STA
// threads at once: opening windows of the same XAML together fails with a PackagePart NullReferenceException or a
// XamlParseException (#1092). Every test here opens its windows on its own STA thread, so the tests run one at a time; this is
// the shared lock for window creation. A test or a temporary load check must not open windows on several threads at once.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
