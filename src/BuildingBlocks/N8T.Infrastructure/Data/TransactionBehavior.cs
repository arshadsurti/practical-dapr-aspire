using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using N8T.Domain;
using System;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace N8T.Infrastructure.Data
{
    public class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly IDomainEventContext _domainEventContext;
        private readonly IDbFacadeResolver _dbFacadeResolver;
        private readonly IMediator _mediator;
        private readonly ILogger<TransactionBehavior<TRequest, TResponse>> _logger;
        private readonly IRequestHandler<TRequest, TResponse> _outerHandler;

        public TransactionBehavior(
            IDbFacadeResolver dbFacadeResolver,
            IDomainEventContext domainEventContext,
            IMediator mediator,
            ILogger<TransactionBehavior<TRequest, TResponse>> logger, 
            IRequestHandler<TRequest, TResponse> outerHandler)
        {
            _domainEventContext = domainEventContext ?? throw new ArgumentNullException(nameof(domainEventContext));
            _dbFacadeResolver = dbFacadeResolver ?? throw new ArgumentNullException(nameof(dbFacadeResolver));
            _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _outerHandler = outerHandler ?? throw new ArgumentNullException(nameof(outerHandler));
        }

        public async Task<TResponse> Handle(TRequest request,
            CancellationToken cancellationToken,
            RequestHandlerDelegate<TResponse> next)
        {
            var transactionAttr = _outerHandler
                .GetType()
                ?.GetTypeInfo()
                ?.GetDeclaredMethod("Handle")
                ?.GetCustomAttributes(typeof(TransactionScopeAttribute), true);

            if (transactionAttr != null && transactionAttr.Length < 1)
            {
                _logger.LogInformation(
                    "Handling {MediatRRequest} with request={MediatRRequestData} and response={MediatRResponseData}",
                    nameof(TransactionBehavior<TRequest, TResponse>), typeof(TRequest).Name, typeof(TResponse).Name);

                return await next();
            }

            _logger.LogInformation("Handling command {MediatRRequest}", typeof(TRequest).FullName);
            _logger.LogDebug("Handling command {MediatRRequest} with content {RequestContent}", typeof(TRequest).FullName, JsonSerializer.Serialize(request));
            _logger.LogInformation("Open the transaction for {MediatRRequest}", typeof(TRequest).FullName);
            var strategy = _dbFacadeResolver.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                // Achieving atomicity
                await using var transaction = _dbFacadeResolver.Database.BeginTransaction(IsolationLevel.ReadCommitted);

                _logger.LogInformation("Executing the {MediatRRequest} request", typeof(TRequest).FullName);
                var response = await next();

                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Publishing domain events for {MediatRRequest}", typeof(TRequest).FullName);
                var domainEvents = _domainEventContext.GetDomainEvents().ToList();

                var tasks = domainEvents
                    .Select(async @event =>
                    {
                        _logger.LogInformation("Publishing domain event {DomainEventName}...", @event.GetType().FullName);
                        _logger.LogDebug(
                            "Publishing domain event {DomainEventName} with payload {DomainEventCotent}", @event.GetType().FullName, JsonSerializer.Serialize(@event));
                        await _mediator.Publish(@event, cancellationToken);
                        _logger.LogInformation("Published domain event {DomainEventName}", @event.GetType().FullName);
                    });

                await Task.WhenAll(tasks);

                _logger.LogInformation("Handled {MediatRRequest}", typeof(TRequest).FullName);
                return response;
            });
        }
    }
}
