using System.Runtime.CompilerServices;

// Tests must exercise the real, registered IActionCatalogue implementations rather than
// hand-rolled duplicates that can silently drift from production role lists.
[assembly: InternalsVisibleTo("AccountantApp.Tests")]
