namespace Microsoft.PowerPlatformLS.Contracts.Lsp.Models
{
    public class Location
    {
        public required Uri Uri { get; set; }
        public required Range Range { get; set; }
    }
}