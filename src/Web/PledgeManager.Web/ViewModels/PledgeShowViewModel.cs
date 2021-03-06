﻿using PledgeManager.Web.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PledgeManager.Web.ViewModels {

    public class PledgeShowViewModel {

        public string PayPalClientId { get; set; }

        public Campaign Campaign { get; set; }

        public Pledge Pledge { get; set; }

        public CampaignReward CurrentReward { get; set; }

        public IEnumerable<(CampaignAddOn AddOn, string Variant)> AddedAddOns { get; set; }

        public IEnumerable<CampaignAddOn> AvailableAddOns { get; set; }

        public IEnumerable<(CampaignReward Reward, decimal UpgradeCost)> UpgradePaths { get; set; }

        public decimal FinalCost { get; set; }

        public decimal ToPay {
            get {
                return Math.Max(0M, FinalCost - Pledge.CurrentPledge);
            }
        }

        public bool CanBeClosed {
            get {
                return Pledge.CurrentPledge >= FinalCost;
            }
        }

        public ConfirmedPayment ConfirmedPayment { get; set; }

        public ErrorNotification Error { get; set; }

        public bool HasError(string key) {
            if(Error != null) {
                return Error.HasError(key);
            }
            return false;
        }

        public string GetRandomCode() {
            return new Random().GenerateReadableCode(8);
        }

    }

}
