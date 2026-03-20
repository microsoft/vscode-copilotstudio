
namespace Microsoft.PowerPlatformLS.Contracts.Lsp.UnitTests
{
    using Microsoft.PowerPlatformLS.Contracts.Lsp.Models;
    using System;
    using Xunit;

    public class PositionTests
    {
        [Fact]
        public void Success_OnEquals()
        {
            var pos1 = new Position { Line = 1, Character = 2 };
            var pos2 = new Position { Line = 1, Character = 2 };
            Assert.Equal(pos1, pos2);

            // same line but different character
            pos2 = new Position { Line = 1, Character = 3 };
            Assert.NotEqual(pos1, pos2);

            // same character but different line
            pos2 = new Position { Line = 2, Character = 2 };
            Assert.NotEqual(pos1, pos2);

            // different line and character
            pos2 = new Position { Line = 3, Character = 5 };
            Assert.NotEqual(pos1, pos2);

            // different types
            Assert.False(pos1.Equals(new object()));
        }
    }
}
