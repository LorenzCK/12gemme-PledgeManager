﻿using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PledgeManager.Web.Models {

    [BsonDiscriminator("shortText")]
    public class SurveyElementShortText : SurveyElementBase {

    }

}
