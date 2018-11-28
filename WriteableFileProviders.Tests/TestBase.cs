using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace WriteableFileProviders.Tests
{
    public abstract class TestBase
    {
        public TestSettings Settings { get; }
        protected TestBase(TestContext context)
        {
            Settings = JsonConvert.DeserializeObject<TestSettings>(
                File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "testsettings.json")));
        }
        protected abstract IWriteableFileProvider GetProvider();

        protected string GetFileName()
        {
            return Guid.NewGuid().ToString();
        }
        [TestMethod]
        public async Task NotExistingEntry()
        {
            var Provider = GetProvider();
            var entry = await Provider.GetFileInfoAsync("nonexisting");
            Assert.IsFalse(entry.Exists, "File is marked as seen");
        }


        [TestMethod]
        public async Task CreateCheckAndDeleteEntry()
        {
            var Provider = GetProvider();
            var name = "test/" + GetFileName();
            var entry = await Provider.GetFileInfoAsync(name);
            if (entry.Exists)
                Assert.Fail("Entry already exists");

            await Provider.UpdateFileAsync(name, new MemoryStream(new[] {(byte) 6}));
            entry = await Provider.GetFileInfoAsync(name);
            if (!entry.Exists)
                Assert.Fail("Entry wasn't created");
            if (entry.Length != 1)
                Assert.Fail("Entry is not 1 bytes long");
            using (var read = entry.CreateReadStream())
            {
                var buf = new byte[1];
                read.Read(buf, 0, 1);
                if (buf[0] != 6)
                    Assert.Fail("File contents were invalid");
            }

            await Provider.UpdateFileAsync(name, new MemoryStream(new[] { (byte)12, (byte)6 }));
            entry = await Provider.GetFileInfoAsync(name);
            if (!entry.Exists)
                Assert.Fail("Entry was deleted after second rewrite");
            if (entry.Length != 2)
                Assert.Fail("Rewritten entry is not 2 bytes long");
            using (var read = entry.CreateReadStream())
            {
                var buf = new byte[2];
                read.Read(buf, 0, 2);
                if (buf[0] != 12 && buf[1] != 6)
                    Assert.Fail("Rewritten file contents were invalid");
            }

            await Provider.DeleteAsync(name);
            entry = await Provider.GetFileInfoAsync(name);
            if (entry.Exists)
                Assert.Fail("Entry wasn't deleted");
        }
        [TestMethod]
        public async Task DeleteDir()
        {
            var Provider = GetProvider();
            var baseDir = $"enumDir{Guid.NewGuid():N}";
            var dir1 = $"{baseDir}/subdir1";
            var dir2 = $"{baseDir}/subdir2";
            var subfile1 = $"{dir1}/file1";
            var subfile2 = $"{dir2}/file1";
            var file1 = $"{baseDir}/file1";
            var file2 = $"{baseDir}/file2";
            await Provider.UpdateFileAsync(subfile1, new MemoryStream(new[] { (byte)6 }));
            await Provider.UpdateFileAsync(subfile2, new MemoryStream(new[] { (byte)6 }));
            await Provider.UpdateFileAsync(file1, new MemoryStream(new[] { (byte)6 }));
            await Provider.UpdateFileAsync(file2, new MemoryStream(new[] { (byte)6 }));


            var entries = (await Provider.GetDirectoryContentsAsync(baseDir)).ToList();
            if (!entries.Exists(e => e.Name == "subdir1" && e.IsDirectory))
                Assert.Fail("Subdir1 not found");
            if (!entries.Exists(e => e.Name == "subdir2" && e.IsDirectory))
                Assert.Fail("Subdir2 not found");
            if (!entries.Exists(e => e.Name == "file1" && !e.IsDirectory))
                Assert.Fail("file1 not found");
            if (!entries.Exists(e => e.Name == "file2" && !e.IsDirectory))
                Assert.Fail("file2 not found");
            var dir = await Provider.GetDirectoryContentsAsync(baseDir);
            if (!dir.Exists)
                Assert.Fail("Directory was not created");
            await Provider.DeleteAsync(baseDir);
            dir = await Provider.GetDirectoryContentsAsync(baseDir);
            if (dir.Exists)
                Assert.Fail("Directory was not deleted");

        }
        [TestMethod]
        public async Task EnumerateDir()
        {
            var Provider = GetProvider();
            var baseDir = $"enumDir{Guid.NewGuid():N}";
            var dir1 = $"{baseDir}/subdir1";
            var dir2 = $"{baseDir}/subdir2";
            var subfile1 = $"{dir1}/file1";
            var subfile2 = $"{dir2}/file1";
            var file1 = $"{baseDir}/file1";
            var file2 = $"{baseDir}/file2";
            await Provider.UpdateFileAsync(subfile1, new MemoryStream(new[] {(byte) 6}));
            await Provider.UpdateFileAsync(subfile2, new MemoryStream(new[] { (byte)6 }));
            await Provider.UpdateFileAsync(file1, new MemoryStream(new[] { (byte)6 }));
            await Provider.UpdateFileAsync(file2, new MemoryStream(new[] { (byte)6 }));
           
            var entries = (await Provider.GetDirectoryContentsAsync(baseDir)).ToList();
            if (!entries.Exists(e => e.Name == "subdir1" && e.IsDirectory))
                Assert.Fail("Subdir1 not found");
            if (!entries.Exists(e => e.Name == "subdir2" && e.IsDirectory))
                Assert.Fail("Subdir2 not found");
            if (!entries.Exists(e => e.Name == "file1" && !e.IsDirectory))
                Assert.Fail("file1 not found");
            if (!entries.Exists(e => e.Name == "file2" && !e.IsDirectory))
                Assert.Fail("file2 not found");

            await Provider.DeleteAsync(baseDir);
        }
        [TestMethod]
        public async Task TestCacheStream()
        {
            var p = GetProvider();
            var name = $"cacheStream/{GetFileName()}";
            var entry = await p.GetFileInfoAsync(name);
            if (entry.Exists)
                Assert.Fail("Entry already exists");
            await p.UpdateFileAsync(name, new MemoryStream(new[] {(byte) 12}));
            entry = await p.GetFileInfoAsync(name);
            if (!entry.Exists)
                Assert.Fail("Entry was not created");
            using (var cacheStream = await p.CreateLocalFileStreamAsync(false, name))
            {
                cacheStream.Write(new[] { (byte)5 }, 0, 1);
                await cacheStream.DisposeAsync();
            }
            entry = await p.GetFileInfoAsync(name);
            if (!entry.Exists)
                Assert.Fail("Entry was Deleted after cache writeback");
            Assert.IsTrue(entry.CreateReadStream().ReadByte() == 5, "File content was not updated via cacheStream");

        }
        [TestMethod]
        public async Task CreateUpdateDelete100MBFile()
        {
            var p = GetProvider();
            var name = $"largefiles/{GetFileName()}";
            var entry = await p.GetFileInfoAsync(name);
            if (entry.Exists)
                Assert.Fail("Entry already exists");
            var oneMBBuffer = new byte[1024 * 1024];
            byte cur = 0;
            for (int i = 0; i < oneMBBuffer.Length; i++)
            {
                oneMBBuffer[i] = cur;
                if (cur == byte.MaxValue)
                    cur = 0;
                else cur++;
            }
            using (var tmpStrm = new FileStream(Path.GetTempFileName(), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                System.IO.FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
                for (var i = 0;i< 100;i++)
                    tmpStrm.Write(oneMBBuffer, 0, oneMBBuffer.Length);
                tmpStrm.Flush(true);
                tmpStrm.Position = 0;
                await p.UpdateFileAsync(name, tmpStrm);
            }
            entry = await p.GetFileInfoAsync(name);
            if (!entry.Exists)
                Assert.Fail("Entry was not created");
            if (entry.Length != oneMBBuffer.Length * 100)
                Assert.Fail("Entry is not 1GB in size");

            var mid1 = oneMBBuffer.Length;
            var mid2 = mid1 + 1;
            using (var localStream = await p.CreateLocalFileStreamAsync(false, name))
            {
                localStream.Position = mid1;
                localStream.Write(new []{(byte)255, (byte)255}, 0, 2);
                localStream.Position = localStream.Length;
                localStream.Write(new[] { (byte)255, (byte)255 }, 0, 2);
            }
            entry = await p.GetFileInfoAsync(name);
            if (!entry.Exists)
                Assert.Fail("Entry was deleted after localupdate");
            if (entry.Length != oneMBBuffer.Length * 100 + 2)
                Assert.Fail("Entry is not 100MB + 2bytes in size");
            using (var openStream = (await p.ReadFileAsync(name)))
            {
                
                byte bmid1 = 0, bmid2 = 0, blast1 = 0, blast2 = 0;
                var last1 = openStream.Length - 2;
                var last2 = last1 + 1;
                var b = new byte[oneMBBuffer.Length];
                long totalRead = 0;
                while (totalRead < entry.Length)
                {
                    var read = openStream.Read(b, 0, b.Length);
                    if (totalRead < mid1 + 1  && totalRead + read >= mid1 + 1)
                        bmid1 = b[mid1 - totalRead];
                    if (totalRead < mid2 + 1 && totalRead + read >= mid2 + 1)
                        bmid2 = b[mid2 - totalRead];
                    if (totalRead < last1 + 1 && totalRead + read >= last1 + 1)
                        blast1 = b[last1 - totalRead];
                    if (totalRead < last2 + 1 && totalRead + read >= last2 +1)
                        blast2 = b[last2 - totalRead];
                    totalRead += read;
                }
                if (bmid1 != 255 || bmid2 != 255)
                    Assert.Fail($"Bytes at index {oneMBBuffer.Length} weren't changed by local stream");
                if (blast2 != 255 || blast1 != 255)
                    Assert.Fail("Bytes at end of stream weren't set by local stream");
               
            }
            await p.DeleteAsync(name);
            entry = await p.GetFileInfoAsync(name);
            if (entry.Exists)
                Assert.Fail("Entry wasn't deleted");

        }
    }
}
