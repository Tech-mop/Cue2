// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Devices;

public interface IDevice
{
    int DeviceId { get; }
    string Name { get; set; }
    
}