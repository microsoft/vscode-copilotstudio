namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using System.Runtime.Serialization;

    // Based on CoreFramework ClusterCategory topology model.
    internal enum CoreServicesClusterCategory
    {
        [EnumMember(Value = "Exp")]
        Exp = 0,

        [EnumMember(Value = "Dev")]
        Dev = 1,

        [EnumMember(Value = "Test")]
        Test = 2,

        [EnumMember(Value = "Preprod")]
        Preprod = 3,

        [EnumMember(Value = "FirstRelease")]
        FirstRelease = 4,

        [EnumMember(Value = "Prod")]
        Prod = 5,

        [EnumMember(Value = "Gov")]
        Gov = 6,

        [EnumMember(Value = "High")]
        High = 7,

        [EnumMember(Value = "DoD")]
        DoD = 8,

        [EnumMember(Value = "Mooncake")]
        Mooncake = 9,

        [EnumMember(Value = "Ex")]
        Ex = 10,

        [EnumMember(Value = "Rx")]
        Rx = 11,

        /// <summary>
        /// Prv is short for Pull Request Validation.
        /// The clusters of this category in <see cref="CoreServicesClusterCategory.Test"/> is used for deploying validation instance during pull request.
        /// </summary>
        [EnumMember(Value = "Prv")]
        Prv = 12,

        [EnumMember(Value = "Local")]
        Local = 13,

        [EnumMember(Value = "GovFR")]
        GovFR = 14,
    }



}
