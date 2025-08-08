//namespace SilicaDB.Storage
//{
//    /// <summary>
//    /// In‐memory representation of a database page.
//    /// Carries its PageId and a fixed‐size byte buffer.
//    /// </summary>
//    public sealed class Page
//    {
//        /// <summary>
//        /// The unique identifier for this page.
//        /// </summary>
//        public PageId Id { get; }

//        /// <summary>
//        /// Raw page data. Length == PageSize.
//        /// </summary>
//        public byte[] Data { get; }

//        /// <summary>
//        /// Size of every page in bytes.  
//        /// Adjust to match your on‐disk format.
//        /// </summary>
//        public const int PageSize = 8192;

//        public Page(PageId id)
//        {
//            Id = id;
//            Data = new byte[PageSize];
//        }
//    }
//}
