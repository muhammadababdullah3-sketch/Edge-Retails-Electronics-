using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.SystemConfiguration;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class EfExceptionToErrorMapper : IExceptionToErrorMapper
{
    public bool TryMap(Exception exception, out Error error)
    {
        if (exception is DbUpdateConcurrencyException concurrency)
        {
            if (concurrency.Entries.Any(entry => entry.Entity is CashSession))
            {
                error = new Error(
                    "cash.session_concurrency_conflict",
                    "Cash session state changed concurrently. Refresh and retry.");
                return true;
            }

            if (concurrency.Entries.Any(entry => entry.Entity is UserSession))
            {
                error = new Error(
                    "identity.session_concurrency_conflict",
                    "Operator session state changed concurrently. Sign in again.");
                return true;
            }

            if (concurrency.Entries.Any(entry => entry.Entity is OutboxMessage))
            {
                error = new Error(
                    "outbox.concurrency_conflict",
                    "Background work conflicted with another processor. Retry shortly.");
                return true;
            }

            if (concurrency.Entries.Count > 0)
            {
                error = new Error(
                    "persistence.concurrency_conflict",
                    "The record changed since it was loaded. Refresh and retry.");
                return true;
            }
        }

        error = default!;
        return false;
    }
}
