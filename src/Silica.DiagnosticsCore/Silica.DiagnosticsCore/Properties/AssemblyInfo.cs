using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Compliance and interop
[assembly: ComVisible(false)]
[assembly: Guid("D6ACF04E-1F59-4D40-A0C9-3E6F779B54AB")] // Unique for this assembly

// Optional: mark as CLS-compliant if you want cross-language .NET compatibility
[assembly: CLSCompliant(true)]


// Allow test project to access internal types like ConsoleTraceSink/FileTraceSink
 [assembly: InternalsVisibleTo("Silica.DiagnosticsCore.Tests")]