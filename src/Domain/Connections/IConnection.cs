// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

namespace Cue2.Domain.Connections;

public interface IConnection
{
    int ConnectionId { get; }
    int Name { get; set; }
}