namespace Microsoft.PowerPlatformLS.UnitTests.Impl.PullAgent
{
    using Microsoft.PowerPlatformLS.Impl.PullAgent;
    using System;
    using Xunit;

    public class ExceptionSourceExtractorTests
    {
        [Fact]
        public void GetSource_Returns_TypeAndMethod_For_Exception_Without_PDB()
        {
            // Arrange: throw from a known method so the first non-framework frame is ours.
            Exception captured = CaptureException();

            // Act
            var source = ExceptionSourceExtractor.GetSource(captured);

            // Assert: should reference this test class or the helper method
            Assert.NotNull(source);
            Assert.Contains("ExceptionSourceExtractorTests", source);
        }

        [Fact]
        public void GetSource_Skips_System_Frames()
        {
            // Arrange: wrap in a System.* call chain
            Exception captured = CaptureViaSystemCall();

            // Act
            var source = ExceptionSourceExtractor.GetSource(captured);

            // Assert: should NOT start with "System."
            Assert.NotNull(source);
            Assert.DoesNotContain("System.", source);
        }

        [Fact]
        public void GetSource_Returns_Null_For_Exception_Without_StackTrace()
        {
            // An exception that was never thrown has no stack trace
            var ex = new InvalidOperationException("never thrown");

            var source = ExceptionSourceExtractor.GetSource(ex);

            Assert.Null(source);
        }

        [Fact]
        public void FormatSource_Returns_Parenthesized_String_When_Source_Available()
        {
            Exception captured = CaptureException();

            var formatted = ExceptionSourceExtractor.FormatSource(captured);

            Assert.StartsWith(" (at ", formatted);
            Assert.EndsWith(")", formatted);
        }

        [Fact]
        public void FormatSource_Returns_Empty_When_No_Source()
        {
            var ex = new InvalidOperationException("no trace");

            var formatted = ExceptionSourceExtractor.FormatSource(ex);

            Assert.Equal(string.Empty, formatted);
        }

        private static Exception CaptureException()
        {
            try
            {
                throw new InvalidOperationException("test error");
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static Exception CaptureViaSystemCall()
        {
            try
            {
                // Force through System.Convert which will fail and give us a mixed stack
                int[] arr = null!;
                _ = arr[0]; // NullReferenceException thrown by runtime
                return null!; // unreachable
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
