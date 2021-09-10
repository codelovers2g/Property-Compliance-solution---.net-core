using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using IdentityModel.Client;
using System.Net.Http;
using Xero.NetStandard.OAuth2.Models;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using System.Linq;
using Xero.NetStandard.OAuth2.Model.Accounting;
using RestSharp;
using Microsoft.Extensions.Options;

namespace PCS.BLL.Services
{
    public class XeroApiService : IXeroApiService
    {
        private readonly IConfiguration _config;
        private readonly IUserService _userService;
        private readonly ISessionService _sessionService;
        private readonly ICommonService _commonService;
        private readonly IJobService _jobService;
        private ILogger<XeroApiService> _logger;
        private readonly IOptions<SmtpConfig> _settings;
        HttpClient _httpClient;
        PCSContext _context;
        public XeroApiService(IConfiguration config, IUserService userService, IOptions<SmtpConfig> settings, ISessionService sessionService,
            ICommonService commonService, ILogger<XeroApiService> logger, IJobService jobService)
        {
            _config = config;
            _httpClient = new HttpClient();
            _userService = userService;
            _settings = settings;
            _sessionService = sessionService;
            _commonService = commonService;
            _logger = logger;
            _jobService = jobService;
        }
        public string GetXeroLoginLink()
        {
            var xeroAuthorizeUri = new RequestUrl(_config["Xero:XeroAuthorizationAPI"].ToString());
            var url = xeroAuthorizeUri.CreateAuthorizeUrl(
             clientId: _config["Xero:ClientId"].ToString(),
             responseType: "code", //hardcoded authorisation code for now.
             redirectUri: _config["Xero:RedirectUri_1"].ToString(),
             state: "123",
             scope: "openid profile email offline_access files accounting.transactions accounting.contacts");

            return url;

        }

        public void SaveXeroTokens(string obtainedXeroAccessToken, string obtainedXeroRefreshToken, string obtainedXeroIdentityToken)
        {
            //ObtainedXeroAccessToken = obtainedXeroAccessToken;
            //ObtainedXeroRefreshToken = obtainedXeroRefreshToken;
            //ObtainedXeroIdentityToken = obtainedXeroIdentityToken;
            _context = new PCSContext();
            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                var xeroToken = unityOfWork.CommonRepository.GetXeroTokenDetails();
                xeroToken.XeroAccessToken = obtainedXeroAccessToken;
                xeroToken.XeroRefreshToken = obtainedXeroRefreshToken;
                xeroToken.XeroIdentityToken = obtainedXeroIdentityToken;

                unityOfWork.Save();
            }

        }

        public string GetTenant()
        {
            string tenant = string.Empty;
            using (IUnityOfWork unityOfWork = new UnityOfWork(new PCSContext()))
            {
                XeroTokens xeroTokens = unityOfWork.CommonRepository.GetXeroTokenDetails();
                ObtainedXeroAccessToken = xeroTokens.XeroAccessToken;
                ObtainedXeroRefreshToken = xeroTokens.XeroRefreshToken;
                ObtainedXeroIdentityToken = xeroTokens.XeroIdentityToken;
            }
            List<Xero.NetStandard.OAuth2.Models.Tenant> tenantList = new List<Xero.NetStandard.OAuth2.Models.Tenant>();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ObtainedXeroAccessToken);
            using (var requestMessage = new HttpRequestMessage(HttpMethod.Get, _config["Xero:XeroGetConnectionsAPI"].ToString()))
            {
                HttpResponseMessage httpResult = _httpClient.SendAsync(requestMessage).Result;

                if (!httpResult.StatusCode.Equals(System.Net.HttpStatusCode.OK))
                {
                    if (!RefreshAccessToken()) return string.Empty;
                }

                tenant = httpResult.Content.ReadAsStringAsync().Result;
                tenantList = JsonConvert.DeserializeObject<List<Xero.NetStandard.OAuth2.Models.Tenant>>(tenant);
            }

            if (tenantList.Count > 0)
            {
                ObtainedTenantId = tenantList.First()?.TenantId.ToString();
            }

            return string.Empty;
        }

        public bool RefreshAccessToken()
        {
            _context = new PCSContext();
            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                XeroTokens xeroTokens = unityOfWork.CommonRepository.GetXeroTokenDetails();
                ObtainedXeroAccessToken = xeroTokens.XeroAccessToken;
                ObtainedXeroRefreshToken = xeroTokens.XeroRefreshToken;
                ObtainedXeroIdentityToken = xeroTokens.XeroIdentityToken;
            }

            var response = _httpClient.RequestRefreshTokenAsync(new RefreshTokenRequest
            {
                Address = _config["Xero:XeroAccessTokenAPI"],
                ClientId = _config["Xero:ClientId"].ToString(),
                ClientSecret = _config["Xero:Secret"].ToString(),
                RefreshToken = ObtainedXeroRefreshToken,
                GrantType = "refresh_token",
                Parameters =
                    {
                        { "scope", "offline_access"}
                    }
            }).Result;

            if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
            {
                // send mail to superAdmin to generate access token.
                _context = new PCSContext();
                using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
                {
                    var superAdmin = unityOfWork.UserRepository.GetSuperAdminByRoleId((int)Roles.SuperAdmin);
                    var emailTemplate = unityOfWork.EmailRepository.GetEmailTemplateByEmailTemplateName("XeroAccessTokenRequest");
                    string body = emailTemplate.Body;
                    //body = body.Replace("{name}", agencyOwner.FirstName + " " + agencyOwner.LastName);
                    body = body.Replace("{url}", GetXeroLoginLink());
                    Email.SendEmailWithAttachment(superAdmin.Email, body, emailTemplate.Subject, _settings);
                }
                return false;
            }

            ObtainedXeroAccessToken = response.AccessToken;
            ObtainedXeroRefreshToken = response.RefreshToken;
            ObtainedXeroIdentityToken = response.IdentityToken;
            ObtainedXeroAccessTokenExpireTime = DateTime.UtcNow.AddSeconds(response.ExpiresIn);

            _context = new PCSContext();
            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                var xeroToken = unityOfWork.CommonRepository.GetXeroTokenDetails();
                xeroToken.XeroAccessToken = response.AccessToken;
                xeroToken.XeroRefreshToken = response.RefreshToken;
                xeroToken.XeroIdentityToken = response.IdentityToken;

                unityOfWork.Save();
            }
            return true;
        }

        /// <summary>
        /// It creates XERO contact id for user.
        /// </summary>
        /// <param name="user"></param>
        /// <returns>XERO contact ID</returns>
        public string CreateXeroContactId(DAL.DataModels.User user)
        {
            _context = new PCSContext();
            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                Contacts contacts = new Contacts();
                List<Contact> contact = new List<Contact>()
                {
                    new Contact
                    {
                        Name = user.FirstName +" "+user.LastName,
                        FirstName = user.FirstName,
                        LastName = user.LastName,
                        EmailAddress = user.Email
                    }
                };
                contacts._Contacts = contact;
                //------------------Calling api----------------------
                var client = new RestClient(_config["Xero:XeroContactAPI"].ToString());
                //client.Timeout = -1;
                var request = new RestRequest(Method.POST);
                request.AddHeader("xero-tenant-id", ObtainedTenantId);
                request.AddHeader("Accept", "application/json");
                request.AddHeader("Authorization", "Bearer " + ObtainedXeroAccessToken);
                request.AddHeader("Content-Type", "application/json");
                //request.AddHeader("Cookie", "_abck=01696B28BE8BD8A75535E84B036605B2~-1~YAAQlQVaaKV/CD53AQAALYdyZgXAE/IRfNG5kZosUA9LpDmoxTeR7zdOtScROXZJxkz4vLISHK44xi+RfvBLrvM0K+ELQ7B5TmWdkyQbgFk894EidI1K9yjZjNBFclM6RMhBcX9BuxUSrOOM2gjBMKHpXfWftoQ5qm67YSriPFWj3s+aYvCk/e2i4uclHfVqjP77ZJ9u5YgtUc29ZUY8pmHtssMESQEZRZm/AL9I6m8I1UeLvZioVkJBLYAwgMFAdGeMevXqHdaJ7orxgMG49ci8rdj9mvM2fk62KPlk2ezTNv8O75Svew==~-1~-1~-1; ak_bmsc=F28E0FE59AB3B8D7E06F82534B735008685A0595DA550000F811256048C8E41D~pltTvXYu4688qaUXwGxTTQLq+p+TbVaYkzzNu6Tl+N8ElAuEZbSxvCA5taSkWKFw/3f8IQkrqdUypFsCUyUYBV4cZT5G4ZgARwH77jja8P/2/KdT3AA1qzrxL42i6B8LUKh6py31VBmmbvoAFjGVf9CaLUNJ19fUZZVcE9HjCW6KM37CM9PkXdDt239yy+2/vPXuzuJvuveVnHxGfYJdhVywqUts04w/K4otSnNdbnj/U=; bm_sz=F099FA7DB4F6A2B788F2FC52ED7E9D54~YAAQlQVaaIC404d3AQAA/jPOkApHHJzW9T/vhNUni397cSXqywDxu7KkRw+eFjfA6AnvATDP3hEIvPKR+tSIC+aoxdop+4kKraa327c2liRpLE3xYts6HmwNpmIhYZMhxXMwmSPtNBmAzhpOXvB6tIrSM11G+HFTshNBAoHAUA+E1RqCCWKAluS9SbA7lA==");
                var data = JsonConvert.SerializeObject(contacts);
                request.AddParameter("application/json", data, ParameterType.RequestBody);
                IRestResponse response = client.Execute(request);

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    return string.Empty;

                //-- update user's XeroContactId --
                XeroRootVM xeroContactsRoot = JsonConvert.DeserializeObject<XeroRootVM>(response.Content);

                user.XeroContactId = xeroContactsRoot.Contacts.FirstOrDefault()?.ContactID.ToString();
                //unityOfWork.Save();
                return user.XeroContactId;
            }


        }

        /// <summary>
        /// This sends invoice to xero and save invoice details in database as well.
        /// </summary>
        /// <param name="BookingId"></param>
        /// <param name="invoiceStatus"></param>
        /// <param name="invoiceAmount"></param>
        /// <param name="dueDate"></param>
        /// <returns></returns>
        public bool SendInvoice(int BookingId, int invoiceStatus, Decimal invoiceAmount, DateTime dueDate)
        {
            _context = new PCSContext();

            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                //-- get new access token.
                RefreshAccessToken();

                var booking = unityOfWork.BookingRepository.GetBookingByBookingID(BookingId);

                if (booking == null)
                    return false;

                var inv = unityOfWork.InvoiceRepository.GetInvoiceByBookingId(booking.BookingId);
                if (inv != null)//-- invoice already sent
                    return false;

                var agentProperty = booking.Property?.AgentProperty;

                if (agentProperty == null || agentProperty.Count() <= 0)
                    return false;

                var agentId = agentProperty.FirstOrDefault().AgentId;

                var user = unityOfWork.UserRepository.GetUserByUserId(agentId);

                if (user == null)
                    return false;


                //if XeroContactId null then create XeroContactId and send invoice to that id
                if (string.IsNullOrWhiteSpace(user.XeroContactId))
                {
                    string userXeroContactId = CreateXeroContactId(user);
                    if (string.IsNullOrEmpty(userXeroContactId))
                    {
                        return false;
                    }

                    user.XeroContactId = userXeroContactId;
                    unityOfWork.Save();
                }

                //-- create invoice --
                var lineItems = new List<LineItem>();

                var bookingService = unityOfWork.BookingRepository.GetBookingServicebyBookingId(BookingId);
                if (bookingService == null || bookingService.Count() <= 0)
                    return false;
                Decimal totalServiceAmount = 0;
                //-- creating items list
                foreach (var service in bookingService)
                {
                    totalServiceAmount += Convert.ToDecimal(service.Service.Price /*+ service.Service.Gst*/);
                    lineItems.Add(new LineItem()
                    {
                        Description = service.Service.ServiceName,
                        Quantity = 1,
                        UnitAmount = Convert.ToDecimal(service.Service.Price),
                        AccountCode = "200",
                        //TaxAmount = Convert.ToDecimal(service.Service.Gst)
                    });
                }

                //-- adding xero contact id 
                var contact = new Contact()
                {
                    ContactID = Guid.Parse(user.XeroContactId)
                };

                //-- initialising invoice object
                var invoice = new Xero.NetStandard.OAuth2.Model.Accounting.Invoice()
                {
                    Type = Xero.NetStandard.OAuth2.Model.Accounting.Invoice.TypeEnum.ACCREC,
                    Contact = contact,
                    Date = DateTime.UtcNow.AddHours(_sessionService.CurrentUserSession.TimezoneOffset / 60),
                    LineItems = lineItems,
                    LineAmountTypes = LineAmountTypes.Inclusive,
                    Status = Xero.NetStandard.OAuth2.Model.Accounting.Invoice.StatusEnum.AUTHORISED
                };

                //--due date of invoice
                if (invoiceStatus == 1)//today
                    invoice.DueDate = DateTime.Today;
                else
                    invoice.DueDate = dueDate;


                var invoiceList = new List<Xero.NetStandard.OAuth2.Model.Accounting.Invoice>();
                invoiceList.Add(invoice);

                var invoices = new Invoices();
                invoices._Invoices = invoiceList;

                //-- calling invoice api
                var client = new RestClient(_config["Xero:XeroInvoiceAPI"].ToString());
                client.Timeout = -1;
                var request = new RestRequest(Method.POST);
                request.AddHeader("xero-tenant-id", ObtainedTenantId);
                request.AddHeader("Accept", "application/json");
                request.AddHeader("Authorization", "Bearer " + ObtainedXeroAccessToken);
                request.AddHeader("Content-Type", "application/json");
                //request.AddHeader("Cookie", "_abck=01696B28BE8BD8A75535E84B036605B2~-1~YAAQlQVaaKV/CD53AQAALYdyZgXAE/IRfNG5kZosUA9LpDmoxTeR7zdOtScROXZJxkz4vLISHK44xi+RfvBLrvM0K+ELQ7B5TmWdkyQbgFk894EidI1K9yjZjNBFclM6RMhBcX9BuxUSrOOM2gjBMKHpXfWftoQ5qm67YSriPFWj3s+aYvCk/e2i4uclHfVqjP77ZJ9u5YgtUc29ZUY8pmHtssMESQEZRZm/AL9I6m8I1UeLvZioVkJBLYAwgMFAdGeMevXqHdaJ7orxgMG49ci8rdj9mvM2fk62KPlk2ezTNv8O75Svew==~-1~-1~-1; ak_bmsc=F28E0FE59AB3B8D7E06F82534B735008685A0595DA550000F811256048C8E41D~pltTvXYu4688qaUXwGxTTQLq+p+TbVaYkzzNu6Tl+N8ElAuEZbSxvCA5taSkWKFw/3f8IQkrqdUypFsCUyUYBV4cZT5G4ZgARwH77jja8P/2/KdT3AA1qzrxL42i6B8LUKh6py31VBmmbvoAFjGVf9CaLUNJ19fUZZVcE9HjCW6KM37CM9PkXdDt239yy+2/vPXuzuJvuveVnHxGfYJdhVywqUts04w/K4otSnNdbnj/U=; bm_sz=F099FA7DB4F6A2B788F2FC52ED7E9D54~YAAQlQVaaIC404d3AQAA/jPOkApHHJzW9T/vhNUni397cSXqywDxu7KkRw+eFjfA6AnvATDP3hEIvPKR+tSIC+aoxdop+4kKraa327c2liRpLE3xYts6HmwNpmIhYZMhxXMwmSPtNBmAzhpOXvB6tIrSM11G+HFTshNBAoHAUA+E1RqCCWKAluS9SbA7lA==");
                var data = JsonConvert.SerializeObject(invoices);
                request.AddParameter("application/json", data, ParameterType.RequestBody);
                IRestResponse response = client.Execute(request);

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    return false;
                }


                //--  save invoice details to database
                XeroRootVM xeroInvoiceRoot = JsonConvert.DeserializeObject<XeroRootVM>(response.Content);
                var invoicesResponse = xeroInvoiceRoot.Invoices.FirstOrDefault();
                DAL.DataModels.Invoice invoiceModel = new DAL.DataModels.Invoice
                {
                    XeroInvoiceId = invoicesResponse.InvoiceID.ToString(),
                    XeroInvoiceNumber = invoicesResponse.InvoiceNumber,
                    BookingId = BookingId,
                    PropertyId = booking.PropertyId,
                    AgentUserId = agentId,
                    TotalAmount = invoicesResponse.Total,
                    PaidAmount = invoicesResponse.AmountPaid,
                    DueAmount = invoicesResponse.AmountDue,
                    IssueDate = invoicesResponse.Date,
                    DueDate = invoicesResponse.DueDate,
                    StatusId = (int)invoicesResponse.Status
                };

                unityOfWork.InvoiceRepository.SaveInvoice(invoiceModel);
                unityOfWork.Save();

                //--update payment if paid or partially paid |1:Paid - 2:PartiallyPaid 
                if (invoiceStatus == 1)
                {
                    //add payment for invoice.
                    AddPaymentOfInvoice(invoicesResponse.InvoiceID.ToString(), totalServiceAmount);
                }
                else if (invoiceStatus == 2)
                {
                    AddPaymentOfInvoice(invoicesResponse.InvoiceID.ToString(), invoiceAmount);
                }

                //send email of invoice
                //SendEmailOfInvoice(BookingId, user, booking.Property);
                return true;
            }
        }
        /// <summary>
        /// It add the payment on xero for the particular invoice.
        /// </summary>
        /// <param name="xeroInvoiceId"></param>
        /// <param name="invoiceAmount"></param>
        /// <returns></returns>
        public AjaxResponse AddPaymentOfInvoice(string xeroInvoiceId, Decimal invoiceAmount)
        {
            _context = new PCSContext();
            var _ajaxResponse = new AjaxResponse();
            var refreshTokenResponse = RefreshAccessToken();

            if (!refreshTokenResponse)
            {
                _ajaxResponse.IsSuccess = false;
                _ajaxResponse.Message = "Token expired! Please try after sometime";
                return _ajaxResponse;
            }

            Payment payment = new Payment
            {
                Invoice = new Xero.NetStandard.OAuth2.Model.Accounting.Invoice { InvoiceID = Guid.Parse(xeroInvoiceId) },
                Amount = invoiceAmount,
                Account = new Account { Code = "200" },
                Date = DateTime.Today
            };

            var client = new RestClient(_config["Xero:XeroPaymentAPI"].ToString());
            client.Timeout = -1;
            var request = new RestRequest(Method.POST);
            request.AddHeader("xero-tenant-id", ObtainedTenantId);
            request.AddHeader("Accept", "application/json");
            request.AddHeader("Authorization", "Bearer " + ObtainedXeroAccessToken);
            request.AddHeader("Content-Type", "application/json");
            var data = JsonConvert.SerializeObject(payment);
            request.AddParameter("application/json", data, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                _ajaxResponse.Message = "Bad Request";
                _ajaxResponse.IsSuccess = false;
                return _ajaxResponse;
            }

            XeroRootVM xeroInvoiceRoot = JsonConvert.DeserializeObject<XeroRootVM>(response.Content);
            var invoicesResponse = xeroInvoiceRoot.Payments.FirstOrDefault().Invoice;
            _context = new PCSContext();

            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                var invoice = unityOfWork.InvoiceRepository.GetInvoiceByXeroInvoiceId(xeroInvoiceId);
                if (invoice != null)
                {
                    invoice.DueAmount = invoicesResponse.AmountDue;
                    invoice.PaidAmount = invoicesResponse.AmountPaid;
                    invoice.StatusId = (int)invoicesResponse.Status;
                }
                unityOfWork.Save();
            };

            _ajaxResponse.IsSuccess = true;
            return _ajaxResponse;
        }

        /// <summary>
        /// This is not used yet. But can be used in SendInvoice function to send email of invoice to the user. 
        /// </summary>
        /// <param name="bookingId"></param>
        /// <param name="agent"></param>
        /// <param name="property"></param>
        /// <returns></returns>
        public bool SendEmailOfInvoice(int bookingId, DAL.DataModels.User agent, Property property)
        {
            _context = new PCSContext();
            EmailTemplates emailTemplates = new EmailTemplates();
            State state = new State();
            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                emailTemplates = unityOfWork.EmailRepository.GetEmailTemplateByEmailTemplateName("InvoicePdfAttachment");
                state = unityOfWork.CommonRepository.GetStateByStateId((int)property.StateId);
            }

            if (agent != null && property != null)
            {
                byte[] invoicePDF = _jobService.DownloadInvoicePDF(bookingId);
                var propertyAddress = property.AddressLine1 + ", " + property.Suburb + ", " + state.StateCode + ", " + property.PostCode;
                var emailTemplate = emailTemplates.Body;
                emailTemplate = emailTemplate.Replace("{name}", agent.FirstName);
                emailTemplate = emailTemplate.Replace("{pdfFor}", "invoice");
                emailTemplate = emailTemplate.Replace("{PropertyAddress}", propertyAddress);
                Email.SendEmailWithAttachment(agent.Email, emailTemplate, emailTemplates.Subject, _settings, null, new List<byte[]>() { invoicePDF });
                return true;
            }

            return false;
        }

        /// <summary>
        /// update the payment on XERO.
        /// </summary>
        /// <param name="invoiceId"></param>
        /// <param name="invoiceAmount"></param>
        /// <returns></returns>
        public AjaxResponse UpdateInvoice(int invoiceId, decimal invoiceAmount)
        {
            _context = new PCSContext();
            var _ajaxResponse = new AjaxResponse();
            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                var invoice = unityOfWork.InvoiceRepository.GetInvoiceByInvoiceId(invoiceId);
                if (invoice == null)
                {
                    _ajaxResponse.IsSuccess = false;
                    _ajaxResponse.Message = "Invoice not found. Something went wrong";
                    return _ajaxResponse;
                }

                return AddPaymentOfInvoice(invoice.XeroInvoiceId, invoiceAmount);
            }
        }

        public void SaveXeroTokenDetails(XeroTokens xeroTokens)
        {
            _context.XeroTokens.Add(xeroTokens);
        }

        /// <summary>
        /// returns status of invoice based on invoice due date
        /// </summary>
        /// <param name="invoiceVM"></param>
        /// <returns></returns>
        public string GetInvoiceStatus(InvoiceVM invoiceVM)
        {
            string status = string.Empty;
            if (invoiceVM == null)
                return string.Empty;

            if (invoiceVM.StatusId == (int)XeroInvoiceStatus.PAID)
                return XeroInvoiceStatus.PAID.ToString();
            
            var currentDate = DateTime.UtcNow.AddHours(_sessionService.CurrentUserSession.TimezoneOffset / 60);

            if (invoiceVM.StatusId == (int)XeroInvoiceStatus.AUTHORISED && invoiceVM.DueDate.Value.Date >= currentDate.Date)
            {
                return "Due";
            }

            if (invoiceVM.StatusId == (int)XeroInvoiceStatus.AUTHORISED && invoiceVM.DueDate.Value.Date < currentDate.Date)
            {
                return "Overdue";
            }

            return string.Empty;
        }

        /// <summary>
        /// It will  update the invoice information in PCS database from the XERO.
        /// </summary>
        /// <param name="pcsInvoiceId"></param>
        /// <returns></returns>
        public AjaxResponse CheckInvoiceStatusOnXERO(int pcsInvoiceId)
        {
            _context = new PCSContext();
            var _ajaxResponse = new AjaxResponse();
            using (IUnityOfWork unityOfWork = new UnityOfWork(_context))
            {
                RefreshAccessToken();
                var invoice = unityOfWork.InvoiceRepository.GetInvoiceByInvoiceId(pcsInvoiceId);
                var api = _config["Xero:XeroInvoiceAPI"].ToString() + "/" + invoice.XeroInvoiceId;
                var client = new RestClient(api);
                client.Timeout = -1;
                var request = new RestRequest(Method.GET);
                request.AddHeader("xero-tenant-id", ObtainedTenantId);
                request.AddHeader("Accept", "application/json");
                request.AddHeader("Authorization", "Bearer " + ObtainedXeroAccessToken);
                request.AddHeader("Content-Type", "application/json");
                IRestResponse response = client.Execute(request);

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    _ajaxResponse.IsSuccess = false;
                    _ajaxResponse.Message = "Something went wrong";
                    return _ajaxResponse;
                }

                XeroRootVM xeroInvoiceRoot = JsonConvert.DeserializeObject<XeroRootVM>(response.Content);
                var invoicesResponse = xeroInvoiceRoot.Invoices.FirstOrDefault();

                if (invoicesResponse.Status == Xero.NetStandard.OAuth2.Model.Accounting.Invoice.StatusEnum.PAID)
                {
                    invoice.DueAmount = 0;
                    invoice.PaidAmount = invoice.TotalAmount;
                    invoice.StatusId = (int)Xero.NetStandard.OAuth2.Model.Accounting.Invoice.StatusEnum.PAID;
                    unityOfWork.Save();
                    _ajaxResponse.IsSuccess = true;
                    _ajaxResponse.Message = "Invoice is syncd with XERO";
                    return _ajaxResponse;
                }
                else if (invoicesResponse.Status == Xero.NetStandard.OAuth2.Model.Accounting.Invoice.StatusEnum.AUTHORISED)
                {
                    invoice.DueAmount = invoicesResponse.AmountDue;
                    invoice.PaidAmount = invoice.PaidAmount;
                    unityOfWork.Save();
                    _ajaxResponse.IsSuccess = true;
                    _ajaxResponse.Message = "Invoice is syncd with XERO";
                    return _ajaxResponse;
                }

                return new AjaxResponse()
                {
                    IsSuccess = false,
                    Message = "Something went wrong"
                };
            }
        }

    }
}
