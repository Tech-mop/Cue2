// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Connections;

public class NetworkConnection : IConnection
{
    public int ConnectionId { get; }
    public int Name { get; set; }
}