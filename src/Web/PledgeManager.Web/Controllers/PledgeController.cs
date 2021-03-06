﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using PledgeManager.Web.Models;
using PledgeManager.Web.ViewModels;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PledgeManager.Web.Controllers {

    public class PledgeController : Controller {

        private readonly MongoDatabase _database;
        private readonly PayPalManager _paypal;
        private readonly MailComposer _composer;
        private readonly ILogger<PledgeController> _logger;

        private const string TempKeyPaymentConfirmation = "TempRedirectPaymentData";
        private const string TempKeyErrorNotification = "TempRedirectErrorNotification";

        public PledgeController(
            MongoDatabase database,
            PayPalManager paypal,
            MailComposer composer,
            ILogger<PledgeController> logger
        ) {
            _database = database;
            _paypal = paypal;
            _composer = composer;
            _logger = logger;
        }

        private async Task<(Campaign, Pledge)> GetPledge(string campaignCode, int userId) {
            var c = await _database.GetCampaign(campaignCode);
            if (c == null) {
                _logger.LogInformation("Campaign {0} does not exist", campaignCode);
                return (null, null);
            }

            var pledge = await _database.GetPledge(c.Id, userId);
            if (pledge == null) {
                _logger.LogInformation("Pledge for user #{0} does not exist", userId);
                return (null, null);
            }

            return (c, pledge);
        }

        private async Task<(Campaign, Pledge, IActionResult)> GetPledgeAndVerify(
            string campaignCode, int userId, string token) {

            _logger.LogDebug("Loading pledge information for campaign {0} and user {1}", campaignCode, userId);

            (var c, var pledge) = await GetPledge(campaignCode, userId);
            if (c == null || pledge == null) {
                return (null, null, NotFound());
            }
            if (!pledge.UserToken.Equals(token)) {
                _logger.LogInformation("Token for user #{0} does not match", userId);
                return (null, null, Unauthorized());
            }

            return (c, pledge, null);
        }

        private IActionResult ShowError(string message, params string[] errorKeys) {
            var notification = new ErrorNotification {
                Message = message
            };
            foreach(var k in errorKeys) {
                notification.AddErrorKey(k);
            }

            this.AddToTemp(TempKeyErrorNotification, notification);

            return RedirectToAction(nameof(Index));
        }

        [Authenticate(Startup.CampaignLoginPolicy)]
        public async Task<IActionResult> Index(
            [FromRoute] string campaignCode,
            [FromRoute] int userId,
            [FromRoute] string token
        ) {
            (var campaign, var pledge, var ret) = await GetPledgeAndVerify(campaignCode, userId, token);
            if (ret != null) {
                return ret;
            }

            var campaignLogin = HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!campaignCode.Equals(campaignLogin, StringComparison.InvariantCultureIgnoreCase)) {
                pledge.LastAccess = DateTime.UtcNow;
                await _database.UpdatePledge(pledge);
            }

            // Rapid access reward and add-on maps
            var rewardMap = campaign.Rewards.ToDictionary(reward => reward.Code);
            var addonMap = campaign.AddOns.ToDictionary(addon => addon.Code);
            var addedAddOns = from addon in pledge.AddOns
                              let campaignAddon = addonMap[addon.Code]
                              select (campaignAddon, addon.Variant);

            // Pick rewards
            var originalReward = rewardMap[pledge.OriginalRewardLevel];
            var currentReward = rewardMap[pledge.CurrentRewardLevel];

            if (pledge.IsClosed) {
                _logger.LogDebug("Showing closed pledge for campaign {0} and user {1}", campaignCode, userId);

                return View("ShowClosed", new PledgeClosedViewModel {
                    Campaign = campaign,
                    Pledge = pledge,
                    CurrentReward = currentReward,
                    AddedAddOns = addedAddOns
                });
            }

            _logger.LogDebug("Showing open pledge for campaign {0} and user {1}", campaignCode, userId);

            // Compute final total cost of pledge
            var finalCost = currentReward.PledgeBase + (from addon in pledge.AddOns
                                                        let campaignAddon = addonMap[addon.Code]
                                                        select campaignAddon.Cost).Sum();

            // Compute rapid access hashmap of excluded add-ons that cannot be added
            var excludedAddOnCodes = new HashSet<string>();
            foreach (var (addon, _) in addedAddOns) {
                if (!addon.MultipleEnabled) {
                    excludedAddOnCodes.Add(addon.Code);
                }
                if (addon.Excludes != null) {
                    excludedAddOnCodes.UnionWith(addon.Excludes);
                }
            }

            var vm = new PledgeShowViewModel {
                PayPalClientId = _paypal.Environment.ClientId(),
                Campaign = campaign,
                Pledge = pledge,
                CurrentReward = currentReward,
                AddedAddOns = addedAddOns,
                AvailableAddOns = from addon in campaign.AddOns
                                  where !excludedAddOnCodes.Contains(addon.Code)
                                  select addon,
                UpgradePaths = from upgrade in originalReward.FullUpgradePaths
                               let campaignUpgrade = rewardMap[upgrade]
                               let upgradeDifference = campaignUpgrade.PledgeBase - currentReward.PledgeBase
                               select (campaignUpgrade, upgradeDifference),
                FinalCost = finalCost + pledge.OriginalShippingPayment
            };

            // Fetch redirect temp data
            vm.ConfirmedPayment = this.FromTemp<ConfirmedPayment>(TempKeyPaymentConfirmation);
            vm.Error = this.FromTemp<ErrorNotification>(TempKeyErrorNotification);

            _logger.LogTrace("Pledge view ready to show");

            return View("Show", vm);
        }

        [HttpPost]
        public async Task<IActionResult> UpdateShipping(
            [FromRoute] string campaignCode,
            [FromRoute] int userId,
            [FromRoute] string token,
            [FromForm] string inputGivenName,
            [FromForm] string inputSurname,
            [FromForm] string inputShippingAddress,
            [FromForm] string inputShippingAddressSecundary,
            [FromForm] string inputShippingZip,
            [FromForm] string inputShippingCity,
            [FromForm] string inputShippingProvince,
            [FromForm] string inputShippingCountry
        ) {
            (var campaign, var pledge, var ret) = await GetPledgeAndVerify(campaignCode, userId, token);
            if (ret != null) {
                return ret;
            }

            _logger.LogInformation("Campaign {0}, user {1}, updating shipping information",
                campaignCode, userId);

            pledge.Shipping = new ShippingInfo {
                GivenName = inputGivenName?.Trim(),
                Surname = inputSurname?.Trim(),
                Address = inputShippingAddress?.Trim(),
                AddressSecondary = inputShippingAddressSecundary?.Trim(),
                ZipCode = inputShippingZip?.Trim(),
                City = inputShippingCity?.Trim(),
                Province = inputShippingProvince?.Trim(),
                Country = inputShippingCountry?.Trim()
            };
            pledge.LastUpdate = DateTime.UtcNow;
            await _database.UpdatePledge(pledge);

            var errors = ValidateShipping(pledge.Shipping);
            if(errors.Any()) {
                return ShowError("Indirizzo di spedizione non valido o non completo.", errors.ToArray());
            }

            return RedirectToAction(nameof(Index), "Pledge", new {
                campaignCode,
                userId,
                token
            }, "shipping");
        }

        [HttpPost]
        public async Task<IActionResult> UpdateReward(
            [FromRoute] string campaignCode,
            [FromRoute] int userId,
            [FromRoute] string token,
            [FromForm] string rewardCode
        ) {
            (var campaign, var pledge, var ret) = await GetPledgeAndVerify(campaignCode, userId, token);
            if (ret != null) {
                return ret;
            }

            if (!campaign.Rewards.Any(r => r.Code == rewardCode)) {
                _logger.LogError("Selected reward level '{0}' does not exist", rewardCode);
                return ShowError("Selected reward level does not exist.");
            }

            _logger.LogInformation("Campaign {0}, user {1}, updating reward level to '{2}'",
                campaignCode, userId, rewardCode);

            pledge.CurrentRewardLevel = rewardCode;
            pledge.LastUpdate = DateTime.UtcNow;
            await _database.UpdatePledge(pledge);

            return RedirectToAction(nameof(Index), "Pledge", new {
                campaignCode,
                userId,
                token
            }, "reward");
        }

        [HttpPost]
        public async Task<IActionResult> AddAddon(
            [FromRoute] string campaignCode,
            [FromRoute] int userId,
            [FromRoute] string token,
            [FromForm] string addonCode,
            [FromForm] string variant
        ) {
            (var campaign, var pledge, var ret) = await GetPledgeAndVerify(campaignCode, userId, token);
            if (ret != null) {
                return ret;
            }

            var addon = campaign.AddOns.Where(a => a.Code == addonCode).SingleOrDefault();
            if (addon == null) {
                _logger.LogError("Selected add-on '{0}' does not exist", addonCode);
                return ShowError("Selected add-on does not exist.");
            }
            if (addon.Variants != null) {
                if (!addon.Variants.Any(v => v == variant)) {
                    _logger.LogError("Selected add-on '{0}' does require variant but none given");
                    return ShowError("Selected add-on requires a variant, but no variant indicator given.");
                }
            }
            else {
                // Fix variant to null in case add-on has no variants
                variant = null;
            }

            var existingCount = pledge.AddOns.Where(a => a.Code == addonCode).Count();
            if (existingCount > 0 && !addon.MultipleEnabled) {
                _logger.LogError("Cannot add multiple add-ons '{0}'", addonCode);
                return ShowError("Cannot add multiple instances of selected add-on.");
            }

            _logger.LogInformation("Campaign {0}, user {1}, adding add-on '{2}' variant '{3}'",
                campaignCode, userId, addonCode, variant);

            pledge.AddOns.Add(new PledgeAddOn {
                Code = addonCode,
                Variant = variant
            });
            pledge.LastUpdate = DateTime.UtcNow;
            await _database.UpdatePledge(pledge);

            return RedirectToAction(nameof(Index), "Pledge", new {
                campaignCode,
                userId,
                token
            }, "addons");
        }

        [HttpPost]
        public async Task<IActionResult> RemoveAddon(
            [FromRoute] string campaignCode,
            [FromRoute] int userId,
            [FromRoute] string token,
            [FromForm] string addonCode,
            [FromForm] string variant
        ) {
            (var campaign, var pledge, var ret) = await GetPledgeAndVerify(campaignCode, userId, token);
            if (ret != null) {
                return ret;
            }

            var addon = (from a in pledge.AddOns
                         where a.Code == addonCode && (variant == null || variant == a.Variant)
                         select a).FirstOrDefault();
            if (addon == null) {
                _logger.LogError("Addon '{0}' not present in pledge, cannot be removed", addonCode);
                return ShowError("Selected add-on not present, cannot be removed.");
            }

            _logger.LogInformation("Campaign {0}, user {1}, removing add-on '{2}' variant '{3}'",
                campaignCode, userId, addonCode, variant);

            pledge.AddOns.Remove(addon);
            pledge.LastUpdate = DateTime.UtcNow;
            await _database.UpdatePledge(pledge);

            return RedirectToAction(nameof(Index), "Pledge", new {
                campaignCode,
                userId,
                token
            }, "addons");
        }

        private static Regex _simpleEmailRegex = new Regex("^.*@\\w+.\\w+$",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private (bool Ok, string Error) ProcessSurveyElement(Pledge pledge, SurveyElementBase element, string value) {
            _logger.LogDebug("Survey element {0} type {1} set to '{2}'", element.Name, element.GetType(), value);

            switch (element) {
                case SurveyElementShortText s:
                    if(string.IsNullOrWhiteSpace(value) && !s.IsOptional) {
                        return (false, $"Per favore compila il campo “{element.Title}” del sondaggio in fondo alla pagina.");
                    }
                    else {
                        if (!string.IsNullOrWhiteSpace(value)) {
                            pledge.Survey.Add(s.Name, value);
                        }
                        return (true, null);
                    }

                case SurveyElementEmailAddress s:
                    if (string.IsNullOrWhiteSpace(value) && !s.IsOptional) {
                        return (false, $"Per favore compila il campo “{element.Title}” del sondaggio in fondo alla pagina.");
                    }
                    else if(!string.IsNullOrWhiteSpace(value) && !_simpleEmailRegex.IsMatch(value)) {
                        return (false, $"Per favore inserisci un indirizzo valido nel campo “{element.Title}” del sondaggio in fondo alla pagina.");
                    }
                    else {
                        if (!string.IsNullOrWhiteSpace(value)) {
                            pledge.Survey.Add(s.Name, value);
                        }
                        return (true, null);
                    }

                case SurveyElementCheckbox s:
                    if(!string.IsNullOrWhiteSpace(value)) {
                        pledge.Survey.Add(s.Name, true);
                    }
                    else {
                        pledge.Survey.Add(s.Name, false);
                    }
                    return (true, null);

                default:
                    throw new ArgumentException();
            }
        }

        [HttpPost]
        public async Task<IActionResult> Close(
            [FromRoute] string campaignCode,
            [FromRoute] int userId,
            [FromRoute] string token,
            [FromForm] string pledgeNotes
        ) {
            (var campaign, var pledge, var ret) = await GetPledgeAndVerify(campaignCode, userId, token);
            if (ret != null) {
                return ret;
            }

            _logger.LogInformation("Campaign {0}, user {1}, attempting to close pledge",
                campaignCode, userId);

            var errors = new List<string>();
            errors.AddRange(ValidateShipping(pledge.Shipping));

            pledge.Survey = new BsonDocument();
            foreach(var surveyElement in campaign.Survey) {
                Request.Form.TryGetValue("survey-" + surveyElement.Name, out var surveyValue);
                (var surveyOk, var surveyError) = ProcessSurveyElement(pledge, surveyElement, surveyValue.ToString());
                if(!surveyOk) {
                    errors.Add("survey-" + surveyElement.Name);
                }
            }

            pledge.Note = pledgeNotes;
            pledge.LastUpdate = DateTime.UtcNow;

            if (errors.Any()) {
                _logger.LogDebug("Campaign {0}, user {1}, failed to close pledge ({2} errors)", errors.Count);
                return ShowError("Alcune informazioni non sono state compilate.", errors.ToArray());
            }
            else {
                _logger.LogDebug("Campaign {0}, user {1}, pledge closed");
                pledge.IsClosed = true;
                _composer.SendClosingConfirmation(campaign, pledge);
            }

            await _database.UpdatePledge(pledge);

            return RedirectToAction(nameof(Index), new {
                campaignCode,
                userId,
                token
            });
        }

        [HttpPost]
        public async Task<IActionResult> ProcessPayment(
            [FromRoute] string campaignCode,
            [FromRoute] int userId,
            [FromRoute] string token,
            [FromForm] string paymentOrderId
        ) {
            (var campaign, var pledge, var ret) = await GetPledgeAndVerify(campaignCode, userId, token);
            if (ret != null) {
                return ret;
            }

            _logger.LogInformation("Campaign {0}, user {1}, processing payment order ID {2}",
                campaignCode, userId, paymentOrderId);

            var reqOrder = new PayPalCheckoutSdk.Orders.OrdersGetRequest(paymentOrderId);
            var response = await _paypal.Client.Execute(reqOrder);
            if(response.StatusCode != HttpStatusCode.OK) {
                _logger.LogError("Failed to fetch order ID {0}, request status {1}", paymentOrderId, response.StatusCode);
                return ShowError($"Failed to fetch order ID {paymentOrderId} status from PayPal. Contact support.");
            }
            var order = response.Result<PayPalCheckoutSdk.Orders.Order>();
            _logger.LogInformation("Campaign {0}, user {1} payment for order ID {2} retrieved with status {3}",
                campaignCode, userId, paymentOrderId, order.Status);

            if(!"COMPLETED".Equals(order.Status, StringComparison.InvariantCultureIgnoreCase)) {
                _logger.LogError("Order ID {0} not completed, marked as {1}", paymentOrderId, order.Status);
                return ShowError($"Order ID {paymentOrderId} is not marked as completed. Contact support.");
            }

            decimal total = 0M;
            foreach(var pu in order.PurchaseUnits) {
                _logger.LogDebug("Purchase unit for {0} {1}",
                    pu.AmountWithBreakdown.Value, pu.AmountWithBreakdown.CurrencyCode);
                if(pu.AmountWithBreakdown.CurrencyCode != "EUR") {
                    _logger.LogWarning("Currency {0} not handled", pu.AmountWithBreakdown.CurrencyCode);
                    continue;
                }
                if(!decimal.TryParse(pu.AmountWithBreakdown.Value, NumberStyles.Currency, CultureInfo.InvariantCulture, out decimal purchaseValue)) {
                    _logger.LogError("Cannot parse purchase unit value {0} {1}", pu.AmountWithBreakdown.Value, pu.AmountWithBreakdown.CurrencyCode);
                    continue;
                }
                total += purchaseValue;
            }

            _logger.LogInformation("Campaign {0}, user {1} added payment order ID {2} for {3} to pledge",
                campaignCode, userId, paymentOrderId, total);

            pledge.CurrentPledge += total;
            pledge.Payments.Add(new PledgePayment {
                OrderId = order.Id,
                PayerId = order.Payer.PayerId,
                PayerEmail = order.Payer.Email,
                Value = total,
                Timestamp = DateTime.UtcNow
            });
            pledge.LastUpdate = DateTime.UtcNow;
            await _database.UpdatePledge(pledge);

            this.AddToTemp(TempKeyPaymentConfirmation, new ConfirmedPayment {
                PaymentId = order.Id,
                PaymentTotal = total
            });

            return RedirectToAction(nameof(Index), "Pledge", new {
                campaignCode,
                userId,
                token
            }, "done");
        }

        private IEnumerable<string> ValidateShipping(ShippingInfo shipping) {
            List<string> errors = new List<string>();

            if (string.IsNullOrWhiteSpace(shipping.GivenName)) {
                errors.Add("inputGivenName");
            }
            if (string.IsNullOrWhiteSpace(shipping.Surname)) {
                errors.Add("inputSurname");
            }
            if (string.IsNullOrWhiteSpace(shipping.Address)) {
                errors.Add("inputShippingAddress");
            }
            if (string.IsNullOrWhiteSpace(shipping.ZipCode)) {
                errors.Add("inputShippingZip");
            }
            if (string.IsNullOrWhiteSpace(shipping.City)) {
                errors.Add("inputShippingCity");
            }
            if (string.IsNullOrWhiteSpace(shipping.Province)) {
                errors.Add("inputShippingProvince");
            }
            if (string.IsNullOrWhiteSpace(shipping.Country)) {
                errors.Add("inputShippingCountry");
            }

            return errors;
        }

    }

}
