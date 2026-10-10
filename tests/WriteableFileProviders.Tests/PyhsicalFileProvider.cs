using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WriteableFileProviders.Physical;

namespace WriteableFileProviders.Tests
{
    [TestClass]
    public class PyhsicalFileProvider:TestBase
    {
        private static TestContext context;
        [ClassInitialize]
        public static void Init(TestContext _context)
        {
            context = _context;

        }
        public PyhsicalFileProvider() : base(context) { }
        protected override IWriteableFileProvider GetProvider()
        {
            var dir = Path.GetFullPath(string.IsNullOrEmpty(Settings.PhysicalFileProvider_RootPath)
                ? Path.Combine(Path.GetTempPath(), "wfp-physical-suite")
                : Settings.PhysicalFileProvider_RootPath);
            Directory.CreateDirectory(dir);
            return new WriteablePhysicalFileProvider(dir);
        }
    }
}
