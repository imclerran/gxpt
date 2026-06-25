using System;
using System.Text;
using Xunit;

namespace CommandMcpServer.Tests
{
    /// <summary>
    /// Pure tests for the PowerShell -EncodedCommand encoder. PowerShell expects base64 of the
    /// UTF-16LE (little-endian) bytes of the script; getting the encoding (UTF-16LE, not UTF-8) right
    /// is the whole point, so these pin it down without needing PowerShell installed. Live discovery
    /// and execution are install-specific and not exercised here.
    /// </summary>
    public class PowerShellToolsTests
    {
        [Fact]
        public void Encodes_as_base64_of_utf16le()
        {
            // "dir" → 64 00 69 00 72 00 (UTF-16LE) → base64 "ZABpAHIA".
            Assert.Equal("ZABpAHIA", PowerShellTools.Encode("dir"));
        }

        [Fact]
        public void Roundtrips_back_to_the_original_script()
        {
            string script = "Get-ChildItem -Path 'C:\\Program Files' |\r\n  Where-Object { $_.Length -gt 0 }";
            byte[] raw = Convert.FromBase64String(PowerShellTools.Encode(script));
            Assert.Equal(script, Encoding.Unicode.GetString(raw));
        }

        [Fact]
        public void Preserves_non_ascii_characters()
        {
            string script = "Write-Output 'café — déjà vu ✓'";
            byte[] raw = Convert.FromBase64String(PowerShellTools.Encode(script));
            Assert.Equal(script, Encoding.Unicode.GetString(raw));
        }

        [Fact]
        public void Empty_and_null_encode_to_empty()
        {
            Assert.Equal(string.Empty, PowerShellTools.Encode(""));
            Assert.Equal(string.Empty, PowerShellTools.Encode(null));
        }
    }
}
