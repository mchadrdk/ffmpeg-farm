﻿using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using System.Web.Http.Filters;

namespace API.WindowsService.Filters
{
    public class ExceptionFilter : ExceptionFilterAttribute
    {
        /// <summary>Raises the exception event.</summary>
        /// <param name="actionExecutedContext">The context for the action.</param>
        public override void OnException(HttpActionExecutedContext actionExecutedContext)
        {
            Exception ex = actionExecutedContext.Exception;

            base.OnException(actionExecutedContext);

            Type type = ex.GetType();
            if (IsException(type))
            {
                var response = actionExecutedContext.Request.CreateErrorResponse(HttpStatusCode.BadRequest, ex.Message);
                throw new HttpResponseException(response);
            }

            if (ex.GetType() == typeof(AggregateException) && ((AggregateException)ex).InnerExceptions.All(x => IsException(x.GetType()))
                && ((AggregateException)ex).InnerExceptions.All(x => x.GetType() == ((AggregateException)ex).InnerExceptions.First().GetType()))
            {
                var response = actionExecutedContext.Request.CreateErrorResponse(HttpStatusCode.BadRequest,
                    ((AggregateException)ex).InnerExceptions.First().Message);

                throw new HttpResponseException(response);
            }
        }

        private static bool IsException(Type type)
        {
            return type == typeof(ArgumentException) || type == typeof(ArgumentNullException) || type == typeof(ArgumentOutOfRangeException)
                || type == typeof(InvalidOperationException);
        }
    }
}