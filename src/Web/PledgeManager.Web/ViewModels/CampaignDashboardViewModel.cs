﻿using PledgeManager.Web.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PledgeManager.Web.ViewModels {
    
    public class CampaignDashboardViewModel {

        public PerformedAction Notification { get; set; }

        public Campaign Campaign { get; set; }

        public IEnumerable<(Pledge Pledge, CampaignReward OriginalReward, CampaignReward CurrentReward)> Pledges { get; set; }

        public decimal TotalOriginalPledges { get; set; }
        public decimal TotalCurrentPledges { get; set; }

        public int PledgeCount { get; set; }
        public int ClosedPledgeCount { get; set; }

    }

}
