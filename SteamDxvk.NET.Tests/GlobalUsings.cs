global using Xunit;
// UseWPF pulls in the WindowsDesktop SDK's own implicit-usings set, which
// doesn't include System.IO the way the plain SDK's does — restore it explicitly.
global using System.IO;
