using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinMtr.Desktop.Tests;

public class MacOsIconTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinMtr.Remaster.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }

    // These are the Windows artifacts from before ICNS generation was added.
    // Refresh them only when an intentional Windows artwork change is approved.
    [Theory]
    [InlineData("src/WinMtr.Desktop/Assets/winmtr-route-pulse-dark.ico", "07426F3BB596BFD72428C57C27D6FB4BCF534EE89917CC85644762D09D20F38A")]
    [InlineData("src/WinMtr.Desktop/Assets/winmtr-route-pulse-light.ico", "7053DED1612097386F6AC59A0853393C305198D9EE8F4FA0B8267D4074075162")]
    [InlineData("src/WinMtr.Desktop/Assets/winmtr-route-pulse.ico", "7053DED1612097386F6AC59A0853393C305198D9EE8F4FA0B8267D4074075162")]
    public void Windows_Icons_Remain_Byte_Stable(string relativePath, string expectedHash)
    {
        byte[] icon = File.ReadAllBytes(Path.Combine(RepositoryRoot(), relativePath));

        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(icon)));
    }

    [Fact]
    public void Committed_Icon_Has_Complete_Icns_Container_With_Png_Frames()
    {
        byte[] icon = File.ReadAllBytes(Path.Combine(
            RepositoryRoot(), "src", "WinMtr.Desktop", "Assets", "winmtr-route-pulse.icns"));
        var requiredFrames = new Dictionary<string, int>
        {
            ["icp4"] = 16,
            ["icp5"] = 32,
            ["icp6"] = 64,
            ["ic07"] = 128,
            ["ic08"] = 256,
            ["ic09"] = 512,
            ["ic10"] = 1024,
            ["ic11"] = 32,
            ["ic12"] = 64,
            ["ic13"] = 256,
            ["ic14"] = 512,
        };
        byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

        Assert.True(icon.Length >= 8, "The ICNS header must be complete.");
        Assert.Equal("icns", Encoding.ASCII.GetString(icon, 0, 4));
        Assert.Equal((uint)icon.Length, BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(4, 4)));

        var chunkTypes = new HashSet<string>();
        var offset = 8;
        long chunkLengthSum = 0;
        while (offset < icon.Length)
        {
            Assert.True(icon.Length - offset >= 8, "Each chunk needs a complete header.");
            string type = Encoding.ASCII.GetString(icon, offset, 4);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(offset + 4, 4));
            Assert.InRange(length, 8u, (uint)(icon.Length - offset));
            Assert.True(chunkTypes.Add(type), $"Duplicate ICNS chunk: {type}.");
            Assert.True(requiredFrames.TryGetValue(type, out int size), $"Unexpected ICNS chunk: {type}.");

            // PNG signature followed by the IHDR length, type, width and height.
            Assert.True(length >= 32, $"The {type} PNG header must be complete.");
            int payload = offset + 8;
            Assert.Equal(pngSignature, icon.AsSpan(payload, 8).ToArray());
            Assert.Equal(13u, BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(payload + 8, 4)));
            Assert.Equal("IHDR", Encoding.ASCII.GetString(icon, payload + 12, 4));
            Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(payload + 16, 4)));
            Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32BigEndian(icon.AsSpan(payload + 20, 4)));

            chunkLengthSum += length;
            offset += (int)length;
        }

        Assert.Equal(icon.Length, offset);
        Assert.Equal(icon.LongLength - 8, chunkLengthSum);
        Assert.Equal(requiredFrames.Keys.Order(), chunkTypes.Order());
    }
}
