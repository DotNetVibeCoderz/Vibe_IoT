using System;
using System.IO;

namespace HomeDashboard.Services
{
    public interface IStorageService
    {
        void SaveFile(string fileName, byte[] data);
        byte[] GetFile(string fileName);
    }

    public class LocalStorageService : IStorageService
    {
        private readonly string _storagePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");

        public LocalStorageService()
        {
            if (!Directory.Exists(_storagePath))
            {
                Directory.CreateDirectory(_storagePath);
            }
        }

        public byte[] GetFile(string fileName)
        {
            var path = Path.Combine(_storagePath, fileName);
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
            return Array.Empty<byte>();
        }

        public void SaveFile(string fileName, byte[] data)
        {
            var path = Path.Combine(_storagePath, fileName);
            File.WriteAllBytes(path, data);
        }
    }
}