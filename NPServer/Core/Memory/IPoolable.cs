﻿namespace NPServer.Core.Memory;

/// <summary>
/// Giao diện cho các đối tượng có thể được lưu trữ trong một <see cref="ObjectPool"/>.
/// </summary>
public interface IPoolable
{
    /// <summary>
    /// Đặt lại một instance <see cref="IPoolable"/> trước khi nó được trả về pool.
    /// </summary>
    public void ResetForPool();
}