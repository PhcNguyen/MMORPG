﻿using NETServer.Infrastructure.Configuration;
using NETServer.Infrastructure.Interfaces;
using NETServer.Application.Helpers;

using System.Security.Cryptography;
using System.Text;

namespace NETServer.Infrastructure.Security
{
    /// <summary>
    /// Lớp RsaCipher cung cấp các chức năng mã hóa và giải mã sử dụng thuật toán RSA.
    /// </summary>
    internal class RsaCipher : IRsaCipher
    {
        private static readonly string ExpiryFilePath = Setting.RsaShelfLifePath;
        private static readonly TimeSpan KeyRotationInterval = Setting.RsaKeyRotationInterval;
        private readonly string PublicKeyFilePath = Setting.RsaPublicKeyFilePath;
        private readonly string PrivateKeyFilePath = Setting.RsaPrivateKeyFilePath;
        private readonly RSA rsa = RSA.Create();

        public RSAParameters PublicKey { get; private set; }
        public RSAParameters PrivateKey { get; private set; }


        /// <summary>
        /// Khởi tạo lớp RsaCipher và tải hoặc tạo khóa RSA mới nếu cần.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (await KeysExist())
            {
                await LoadKeys();
                if (await IsKeyExpired()) await GenerateAndStoreKeys();
            }
            else
            {
                await GenerateAndStoreKeys();
            }
        }

        /// <summary>
        /// Kiểm tra xem các tập tin khóa RSA có tồn tại hay không.
        /// </summary>
        /// <returns>True nếu tất cả các tập tin khóa tồn tại, ngược lại là False.</returns>
        private async Task<bool> KeysExist() =>
            await Task.WhenAll(
                FileHelper.FileExistsAsync(PublicKeyFilePath),
                FileHelper.FileExistsAsync(PrivateKeyFilePath),
                FileHelper.FileExistsAsync(ExpiryFilePath)
            ).ContinueWith(tasks => tasks.Result.All(x => x));

        /// <summary>
        /// Kiểm tra xem khóa RSA đã hết hạn hay chưa.
        /// </summary>
        /// <returns>True nếu khóa đã hết hạn, ngược lại là False.</returns>
        private static async Task<bool> IsKeyExpired()
        {
            var expiryDateStr = await FileHelper.ReadFromFileAsync(ExpiryFilePath);
            return DateTime.Now > DateTime.Parse(expiryDateStr);
        }

        /// <summary>
        /// Tạo và lưu trữ cặp khóa RSA mới, đồng thời cập nhật ngày hết hạn.
        /// </summary>
        /// <param name="keySize">Kích thước của khóa RSA (mặc định là 2048 bit).</param>
        private async Task GenerateAndStoreKeys(int keySize = 2048)
        {
            using var rsa = RSA.Create();
            rsa.KeySize = keySize;
            PublicKey = rsa.ExportParameters(false);
            PrivateKey = rsa.ExportParameters(true);

            await SaveKeys();
            await FileHelper.WriteToFileAsync(ExpiryFilePath, DateTime.Now.Add(KeyRotationInterval).ToString());
        }

        /// <summary>
        /// Lưu trữ khóa công khai và bí mật vào các tập tin tương ứng.
        /// </summary>
        private async Task SaveKeys()
        {
            await FileHelper.WriteToFileAsync(PublicKeyFilePath, Convert.ToBase64String(rsa.ExportRSAPublicKey()));
            await FileHelper.WriteToFileAsync(PrivateKeyFilePath, Convert.ToBase64String(rsa.ExportRSAPrivateKey()));
        }

        /// <summary>
        /// Tải khóa công khai và khóa bí mật từ các tập tin tương ứng.
        /// </summary>
        private async Task LoadKeys()
        {
            var publicKeyContent = await FileHelper.ReadFromFileAsync(PublicKeyFilePath);
            var privateKeyContent = await FileHelper.ReadFromFileAsync(PrivateKeyFilePath);

            rsa.ImportRSAPublicKey(Convert.FromBase64String(publicKeyContent), out _);
            rsa.ImportRSAPrivateKey(Convert.FromBase64String(privateKeyContent), out _);
            PublicKey = rsa.ExportParameters(false);
            PrivateKey = rsa.ExportParameters(true);
        }

        /// <summary>
        /// Mã hóa văn bản bằng khóa công khai của máy chủ.
        /// </summary>
        /// <param name="plaintext">Chuỗi văn bản cần mã hóa.</param>
        /// <returns>Mảng byte chứa dữ liệu đã được mã hóa.</returns>
        public byte[] Encrypt(string plaintext)
        {
            using var rsaEncryptor = RSA.Create();
            rsaEncryptor.ImportParameters(PublicKey);
            return rsaEncryptor.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.Pkcs1);
        }

        /// <summary>
        /// Giải mã dữ liệu đã mã hóa bằng khóa bí mật của máy chủ.
        /// </summary>
        /// <param name="encryptedData">Mảng byte chứa dữ liệu đã mã hóa.</param>
        /// <returns>Chuỗi văn bản đã được giải mã.</returns>
        public string Decrypt(byte[] encryptedData) =>
            Encoding.UTF8.GetString(rsa.Decrypt(encryptedData, RSAEncryptionPadding.Pkcs1));

        public static byte[] ExportPublicKey(RSAParameters publicKey)
        {
            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(publicKey.Modulus?.Length ?? 0);
                writer.Write(publicKey.Modulus ?? []);
                writer.Write(publicKey.Exponent?.Length ?? 0);
                writer.Write(publicKey.Exponent ?? []);
            }
            return ms.ToArray();
        }

        public static RSAParameters ImportPublicKey(byte[] publicKeyBytes)
        {
            using var ms = new MemoryStream(publicKeyBytes);
            using var reader = new BinaryReader(ms);
            // Đọc các tham số từ mảng byte
            RSAParameters rsaKeyInfo = new()
            {
                Modulus = reader.ReadBytes(reader.ReadInt32()),
                Exponent = reader.ReadBytes(reader.ReadInt32())
            };
            return rsaKeyInfo;
        }

        /// <summary>
        /// Mã hóa văn bản bằng khóa công khai của client.
        /// </summary>
        /// <param name="plaintextBytes">Chuỗi văn bản cần mã hóa.</param>
        /// <param name="publicKeyClient">Khóa công khai của client.</param>
        /// <returns>Mảng byte chứa dữ liệu đã được mã hóa.</returns>
        public static byte[] Encrypt(byte[] plaintextBytes, RSAParameters publicKeyClient)
        {
            using var rsaEncryptor = RSA.Create();
            rsaEncryptor.ImportParameters(publicKeyClient);
            return rsaEncryptor.Encrypt(plaintextBytes, RSAEncryptionPadding.Pkcs1);
        }

        /// <summary>
        /// Giải mã dữ liệu đã mã hóa bằng khóa bí mật của máy chủ.
        /// </summary>
        /// <param name="ciphertext">Mảng byte chứa dữ liệu đã mã hóa.</param>
        /// <param name="privateKeyClient">Khóa riêng tư của client.</param>
        /// <returns>Chuỗi văn bản đã được giải mã.</returns>
        public static byte[] Decrypt(byte[] ciphertext, RSAParameters privateKeyClient)
        {
            using var rsaDecryptor = RSA.Create();
            rsaDecryptor.ImportParameters(privateKeyClient);
            return rsaDecryptor.Decrypt(ciphertext, RSAEncryptionPadding.Pkcs1);
        }
    }
}