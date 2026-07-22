using System.Collections;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PosApp.Localization;

namespace PosApp.Wpf.Helpers;

/// <summary>
/// Localizes text created in code (dialogs, validation errors, and generated
/// controls). XAML continues to use DynamicResource normally.
/// </summary>
public static class RuntimeUiText
{
    private sealed record TranslationMaps(
        IReadOnlyDictionary<string, string> EnglishToBengali,
        IReadOnlyDictionary<string, string> BengaliToEnglish);

    private static readonly Lazy<TranslationMaps> Maps = new(BuildMaps);

    private static readonly IReadOnlyDictionary<string, string> SupplementalBengali =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Access denied"] = "প্রবেশাধিকার নেই",
            ["Backup"] = "ব্যাকআপ",
            ["Confirm"] = "নিশ্চিত করুন",
            ["Contact"] = "যোগাযোগ",
            ["Dashboard"] = "ড্যাশবোর্ড",
            ["Discount"] = "ছাড়",
            ["Error"] = "ত্রুটি",
            ["Exit"] = "বের হোন",
            ["Export"] = "রপ্তানি",
            ["Promotion"] = "প্রমোশন",
            ["Purchase"] = "ক্রয়",
            ["Quantity"] = "পরিমাণ",
            ["Recall"] = "পুনরুদ্ধার",
            ["Refund"] = "ফেরত",
            ["Custom Refund"] = "কাস্টম ফেরত",
            ["Invalid refund quantity"] = "ফেরত পরিমাণ সঠিক নয়",
            ["Reset"] = "পুনঃসেট",
            ["Sale Completed"] = "বিক্রয় সম্পন্ন",
            ["Settings"] = "সেটিংস",
            ["Suspended"] = "স্থগিত",
            ["Void"] = "বাতিল",
            ["Warning"] = "সতর্কতা",
            ["Restore cloud data"] = "ক্লাউড ডেটা পুনরুদ্ধার",
            ["Cloud restore complete"] = "ক্লাউড পুনরুদ্ধার সম্পন্ন",
            ["Cloud restore failed"] = "ক্লাউড পুনরুদ্ধার ব্যর্থ",
            ["Cloud synchronization failed"] = "ক্লাউড সিঙ্ক ব্যর্থ",
            ["Cloud connection failed"] = "ক্লাউড সংযোগ ব্যর্থ",
            ["Unable to upload initial snapshot"] = "প্রাথমিক স্ন্যাপশট আপলোড করা যায়নি",
            ["Restore the latest cloud snapshots? This creates a backup, then replaces all local store data. PosApp will close after a successful restore."] = "সর্বশেষ ক্লাউড স্ন্যাপশট পুনরুদ্ধার করবেন? আগে একটি ব্যাকআপ তৈরি হবে, তারপর সব স্থানীয় স্টোর ডেটা প্রতিস্থাপন করা হবে। সফল হলে PosApp বন্ধ হবে।",
            ["Cloud account is not connected. Local checkout remains available."] = "ক্লাউড অ্যাকাউন্ট সংযুক্ত নয়। স্থানীয় বিক্রয় চালু থাকবে।",
            ["Cloud account connected. Offline-first synchronization is available."] = "ক্লাউড অ্যাকাউন্ট সংযুক্ত। অফলাইন-ফার্স্ট সিঙ্ক চালু আছে।",
            ["Cloud account created and this device was registered."] = "ক্লাউড অ্যাকাউন্ট তৈরি হয়েছে এবং এই ডিভাইস নিবন্ধিত হয়েছে।",
            ["Signed in and this device was registered."] = "সাইন ইন হয়েছে এবং এই ডিভাইস নিবন্ধিত হয়েছে।",
            ["Cloud account is not connected."] = "ক্লাউড অ্যাকাউন্ট সংযুক্ত নয়।",
            ["Synchronization is active, but one or more changes require review."] = "সিঙ্ক চালু আছে, তবে এক বা একাধিক পরিবর্তন পর্যালোচনা দরকার।",
            ["Local changes are waiting to synchronize."] = "স্থানীয় পরিবর্তনগুলো সিঙ্কের অপেক্ষায় আছে।",
            ["Cloud synchronization is up to date."] = "ক্লাউড সিঙ্ক হালনাগাদ আছে।",
            ["Checkout failed"] = "চেকআউট ব্যর্থ",
            ["Confirm Refund"] = "ফেরত নিশ্চিত করুন",
            ["Confirm Void"] = "বাতিল নিশ্চিত করুন",
            ["CSV Export"] = "CSV রপ্তানি",
            ["Invalid date range"] = "ভুল তারিখের পরিসর",
            ["Invalid product"] = "পণ্যের তথ্য ভুল",
            ["Invalid user"] = "ব্যবহারকারীর তথ্য ভুল",
            ["Inventory Count"] = "ইনভেন্টরি গণনা",
            ["Post Inventory Count"] = "ইনভেন্টরি গণনা পোস্ট করুন",
            ["Register Required"] = "রেজিস্টার প্রয়োজন",
            ["Restore Backup"] = "ব্যাকআপ পুনরুদ্ধার",
            ["Restore Ready"] = "পুনরুদ্ধার প্রস্তুত",
            ["Safe PosApp Update"] = "নিরাপদ PosApp আপডেট",
            ["Suspend failed"] = "স্থগিত করা যায়নি",
            ["Test Print"] = "পরীক্ষামূলক প্রিন্ট",
            ["Unable to load POS"] = "POS লোড করা যায়নি",
            ["Update stopped"] = "আপডেট বন্ধ হয়েছে",
            ["Void Order"] = "অর্ডার বাতিল",
            ["Your account does not have permission to open this page."] = "এই পৃষ্ঠা খোলার অনুমতি আপনার অ্যাকাউন্টে নেই।",
            ["Exit PosApp?"] = "PosApp থেকে বের হবেন?",
            ["Select a receipt line first."] = "আগে রসিদের একটি লাইন নির্বাচন করুন।",
            ["Void the current order? No sale will be recorded."] = "বর্তমান অর্ডার বাতিল করবেন? কোনো বিক্রয় সংরক্ষিত হবে না।",
            ["Open the cash register before completing a sale."] = "বিক্রয় সম্পন্ন করার আগে ক্যাশ রেজিস্টার খুলুন।",
            ["Sale suspended. Use Recall to continue later."] = "বিক্রয় স্থগিত হয়েছে। পরে চালিয়ে যেতে পুনরুদ্ধার ব্যবহার করুন।",
            ["No suspended sales to recall."] = "পুনরুদ্ধারের জন্য কোনো স্থগিত বিক্রয় নেই।",
            ["Product catalog exported."] = "পণ্যের তালিকা রপ্তানি হয়েছে।",
            ["Name is required."] = "নাম আবশ্যক।",
            ["Select a category."] = "একটি বিভাগ নির্বাচন করুন।",
            ["Tax rate must be between 0 and 100."] = "ট্যাক্স হার ০ থেকে ১০০-এর মধ্যে হতে হবে।",
            ["Enter a name and valid value."] = "একটি নাম ও সঠিক মান লিখুন।",
            ["Maximum uses must be blank or a positive whole number."] = "সর্বোচ্চ ব্যবহার ফাঁকা অথবা ধনাত্মক পূর্ণসংখ্যা হতে হবে।",
            ["Select a product."] = "একটি পণ্য নির্বাচন করুন।",
            ["Enter a positive quantity and valid cost/tax values."] = "ধনাত্মক পরিমাণ এবং সঠিক খরচ/ট্যাক্স লিখুন।",
            ["Add at least one product."] = "অন্তত একটি পণ্য যোগ করুন।",
            ["Deactivate this supplier? Existing purchase history will be kept."] = "এই সরবরাহকারীকে নিষ্ক্রিয় করবেন? আগের ক্রয় ইতিহাস রাখা হবে।",
            ["Enter a valid positive quantity"] = "সঠিক ধনাত্মক পরিমাণ লিখুন",
            ["You cannot deactivate the account that is currently signed in."] = "বর্তমানে সাইন ইন করা অ্যাকাউন্ট নিষ্ক্রিয় করা যাবে না।",
            ["At least one active administrator account is required."] = "অন্তত একটি সক্রিয় অ্যাডমিনিস্ট্রেটর অ্যাকাউন্ট আবশ্যক।",
            ["PIN reset successfully."] = "PIN সফলভাবে পুনঃসেট হয়েছে।",
            ["Cannot delete your own account."] = "নিজের অ্যাকাউন্ট মুছতে পারবেন না।",
            ["Cannot delete the last admin account."] = "শেষ অ্যাডমিন অ্যাকাউন্ট মুছতে পারবেন না।",
            ["Username and full name are required."] = "ব্যবহারকারী নাম ও পূর্ণ নাম আবশ্যক।",
            ["Username cannot contain spaces."] = "ব্যবহারকারী নামে স্পেস থাকতে পারবে না।",
            ["Another user already has this username."] = "অন্য একজন ব্যবহারকারী ইতিমধ্যে এই নাম ব্যবহার করছেন।",
            ["You cannot deactivate or change the role of the account currently signed in."] = "বর্তমানে সাইন ইন করা অ্যাকাউন্ট নিষ্ক্রিয় বা এর ভূমিকা পরিবর্তন করা যাবে না।",
            ["The To date cannot be earlier than the From date."] = "শেষ তারিখ শুরুর তারিখের আগে হতে পারবে না।",
            ["A manager or administrator must close the register."] = "ম্যানেজার বা অ্যাডমিনিস্ট্রেটরকে রেজিস্টার বন্ধ করতে হবে।",
            ["Close this register session and produce the final Z report?"] = "এই রেজিস্টার সেশন বন্ধ করে চূড়ান্ত Z রিপোর্ট তৈরি করবেন?",
            ["Enter a reason."] = "একটি কারণ লিখুন।",
            ["Only completed sales can be refunded."] = "শুধু সম্পন্ন বিক্রয় ফেরত দেওয়া যাবে।",
            ["This sale has already been fully refunded."] = "এই বিক্রয়ের সম্পূর্ণ টাকা ইতিমধ্যে ফেরত দেওয়া হয়েছে।",
            ["Select at least one item to refund."] = "ফেরত দেওয়ার জন্য অন্তত একটি পণ্য নির্বাচন করুন।",
            ["Select a refund payment method."] = "ফেরতের একটি পেমেন্ট মাধ্যম নির্বাচন করুন।",
            ["Refund processed."] = "ফেরত সম্পন্ন হয়েছে।",
            ["Sale voided."] = "বিক্রয় বাতিল হয়েছে।",
            ["Enter at least one counted quantity."] = "অন্তত একটি গণনা করা পরিমাণ লিখুন।",
            ["Counted quantities cannot be negative."] = "গণনা করা পরিমাণ ঋণাত্মক হতে পারবে না।",
            ["Inventory count posted."] = "ইনভেন্টরি গণনা পোস্ট হয়েছে।",
            ["Settings saved."] = "সেটিংস সংরক্ষিত হয়েছে।",
            ["The selected backup will replace the current data on the next start. PosApp will preserve a safety copy of the current database and then close. Continue?"] = "পরবর্তী চালুর সময় নির্বাচিত ব্যাকআপ বর্তমান ডেটা প্রতিস্থাপন করবে। PosApp বর্তমান ডেটাবেসের একটি নিরাপত্তা কপি রেখে বন্ধ হবে। চালিয়ে যাবেন?",
            ["Backup validated and staged. PosApp will now close; start it again to finish the restore."] = "ব্যাকআপ যাচাই করে প্রস্তুত করা হয়েছে। PosApp এখন বন্ধ হবে; পুনরুদ্ধার শেষ করতে আবার চালু করুন।",
            ["A signed-in user is required."] = "সাইন ইন করা ব্যবহারকারী আবশ্যক।",
            ["A payment is required to complete the sale."] = "বিক্রয় সম্পন্ন করতে পেমেন্ট আবশ্যক।",
            ["Payment amounts must be greater than zero."] = "পেমেন্টের পরিমাণ শূন্যের বেশি হতে হবে।",
            ["Applied payments must equal the sale total."] = "প্রয়োগ করা পেমেন্ট বিক্রয়ের মোটের সমান হতে হবে।",
            ["The received amount is less than the applied payments."] = "গ্রহণ করা টাকা প্রয়োগ করা পেমেন্টের চেয়ে কম।",
            ["Only cash payments can produce change."] = "শুধু নগদ পেমেন্টে খুচরা ফেরত দেওয়া যায়।",
            ["Open the cash register before processing a refund that returns cash."] = "নগদ ফেরত দেওয়ার আগে ক্যাশ রেজিস্টার খুলুন।",
            ["Cannot save an empty cart."] = "খালি কার্ট সংরক্ষণ করা যায় না।",
            ["The cart contains an invalid product, quantity, price, tax, or discount."] = "কার্টে ভুল পণ্য, পরিমাণ, মূল্য, ট্যাক্স বা ছাড় আছে।",
            ["A refunded sale cannot be voided."] = "ফেরত দেওয়া বিক্রয় বাতিল করা যাবে না।",
            ["A register session is already open."] = "একটি রেজিস্টার সেশন ইতিমধ্যে খোলা আছে।",
            ["A selected promotion no longer exists."] = "নির্বাচিত প্রমোশনটি আর নেই।",
            ["Add at least one product to the purchase."] = "ক্রয়ে অন্তত একটি পণ্য যোগ করুন।",
            ["Administrator PIN must contain 4 to 12 digits."] = "অ্যাডমিনিস্ট্রেটর PIN-এ ৪ থেকে ১২টি সংখ্যা থাকতে হবে।",
            ["Administrator name must contain 2 to 100 characters."] = "অ্যাডমিনিস্ট্রেটরের নামে ২ থেকে ১০০টি অক্ষর থাকতে হবে।",
            ["Another category already uses this name."] = "অন্য একটি বিভাগ ইতিমধ্যে এই নাম ব্যবহার করছে।",
            ["Another promotion already uses this code."] = "অন্য একটি প্রমোশন ইতিমধ্যে এই কোড ব্যবহার করছে।",
            ["Backup retention must be a whole number from 1 to 200."] = "ব্যাকআপ রাখার সংখ্যা ১ থেকে ২০০-এর মধ্যে পূর্ণসংখ্যা হতে হবে।",
            ["Barcode is already used by another product."] = "বারকোডটি অন্য একটি পণ্যে ব্যবহৃত হচ্ছে।",
            ["Cash amount must be greater than zero."] = "নগদ টাকার পরিমাণ শূন্যের বেশি হতে হবে।",
            ["Category color must use #RRGGBB format."] = "বিভাগের রঙ #RRGGBB বিন্যাসে হতে হবে।",
            ["Category in use by products"] = "বিভাগটি পণ্যে ব্যবহৃত হচ্ছে",
            ["Category name is required and cannot exceed 100 characters."] = "বিভাগের নাম আবশ্যক এবং ১০০ অক্ষরের বেশি হতে পারবে না।",
            ["Category not found"] = "বিভাগ পাওয়া যায়নি",
            ["Choose a path different from the live database."] = "চলমান ডেটাবেস থেকে আলাদা একটি পথ বেছে নিন।",
            ["Counted cash cannot be negative."] = "গণনা করা নগদ টাকা ঋণাত্মক হতে পারবে না।",
            ["Currency code must contain 3 to 8 characters."] = "মুদ্রা কোডে ৩ থেকে ৮টি অক্ষর থাকতে হবে।",
            ["Currency decimal places must be a whole number from 0 to 4."] = "মুদ্রার দশমিক ঘর ০ থেকে ৪-এর মধ্যে পূর্ণসংখ্যা হতে হবে।",
            ["Currency symbol is required and cannot exceed 8 characters."] = "মুদ্রার চিহ্ন আবশ্যক এবং ৮ অক্ষরের বেশি হতে পারবে না।",
            ["Currency symbol must contain 1 to 8 characters."] = "মুদ্রার চিহ্নে ১ থেকে ৮টি অক্ষর থাকতে হবে।",
            ["Customer balances and loyalty rate cannot be negative."] = "গ্রাহকের ব্যালান্স ও লয়্যালটি হার ঋণাত্মক হতে পারবে না।",
            ["Customer not found."] = "গ্রাহক পাওয়া যায়নি।",
            ["Default tax must be from 0 to 100."] = "ডিফল্ট ট্যাক্স ০ থেকে ১০০-এর মধ্যে হতে হবে।",
            ["Enter a reason for the cash movement."] = "নগদ লেনদেনের কারণ লিখুন।",
            ["Enter a valid promotion value."] = "সঠিক প্রমোশন মান লিখুন।",
            ["Insufficient stock"] = "পর্যাপ্ত স্টক নেই",
            ["Maximum uses cannot be negative."] = "সর্বোচ্চ ব্যবহার ঋণাত্মক হতে পারবে না।",
            ["Message duration must be a whole number from 1 to 60 seconds."] = "বার্তার সময়কাল ১ থেকে ৬০ সেকেন্ডের মধ্যে পূর্ণসংখ্যা হতে হবে।",
            ["Only completed sales can be voided."] = "শুধু সম্পন্ন বিক্রয় বাতিল করা যাবে।",
            ["Open the register before adding or removing cash."] = "নগদ টাকা যোগ বা সরানোর আগে রেজিস্টার খুলুন।",
            ["Opening cash cannot be negative."] = "প্রারম্ভিক নগদ টাকা ঋণাত্মক হতে পারবে না।",
            ["Price and cost cannot be negative."] = "মূল্য ও খরচ ঋণাত্মক হতে পারবে না।",
            ["Product columns must be a whole number from 2 to 10."] = "পণ্য কলামের সংখ্যা ২ থেকে ১০-এর মধ্যে পূর্ণসংখ্যা হতে হবে।",
            ["Product name is required and cannot exceed 100 characters."] = "পণ্যের নাম আবশ্যক এবং ১০০ অক্ষরের বেশি হতে পারবে না।",
            ["Product not found"] = "পণ্য পাওয়া যায়নি",
            ["Product rows must be a whole number from 2 to 10."] = "পণ্য সারির সংখ্যা ২ থেকে ১০-এর মধ্যে পূর্ণসংখ্যা হতে হবে।",
            ["Promotion name is required."] = "প্রমোশনের নাম আবশ্যক।",
            ["Promotion not found."] = "প্রমোশন পাওয়া যায়নি।",
            ["Purchase quantities must be positive, costs cannot be negative, and tax must be between 0 and 100."] = "ক্রয়ের পরিমাণ ধনাত্মক, খরচ শূন্য বা বেশি এবং ট্যাক্স ০ থেকে ১০০-এর মধ্যে হতে হবে।",
            ["Receipt width must be a whole number from 40 to 120 mm."] = "রসিদের প্রস্থ ৪০ থেকে ১২০ মিমি-এর মধ্যে পূর্ণসংখ্যা হতে হবে।",
            ["Register session not found."] = "রেজিস্টার সেশন পাওয়া যায়নি।",
            ["SKU and barcode cannot exceed 64 characters."] = "SKU ও বারকোড ৬৪ অক্ষরের বেশি হতে পারবে না।",
            ["SKU is already used by another product."] = "SKU-টি অন্য একটি পণ্যে ব্যবহৃত হচ্ছে।",
            ["Sale not found."] = "বিক্রয় পাওয়া যায়নি।",
            ["Stock and low-stock threshold cannot be negative."] = "স্টক ও কম-স্টক সীমা ঋণাত্মক হতে পারবে না।",
            ["Store name is required."] = "দোকানের নাম আবশ্যক।",
            ["Store name must contain 2 to 100 characters."] = "দোকানের নামে ২ থেকে ১০০টি অক্ষর থাকতে হবে।",
            ["Supplier not found."] = "সরবরাহকারী পাওয়া যায়নি।",
            ["Suspended sale not found."] = "স্থগিত বিক্রয় পাওয়া যায়নি।",
            ["Tax must be between 0 and 100."] = "ট্যাক্স ০ থেকে ১০০-এর মধ্যে হতে হবে।",
            ["That username is already used by another account."] = "ব্যবহারকারী নামটি অন্য একটি অ্যাকাউন্টে ব্যবহৃত হচ্ছে।",
            ["The To date cannot be before the From date."] = "শেষ তারিখ শুরুর তারিখের আগে হতে পারবে না।",
            ["The end date cannot be before the start date."] = "শেষ তারিখ শুরুর তারিখের আগে হতে পারবে না।",
            ["The end date cannot be earlier than the start date."] = "শেষ তারিখ শুরুর তারিখের আগে হতে পারবে না।",
            ["The sale total cannot be negative."] = "বিক্রয়ের মোট ঋণাত্মক হতে পারবে না।",
            ["The sale's register session could not be found."] = "বিক্রয়টির রেজিস্টার সেশন পাওয়া যায়নি।",
            ["The selected database backup is empty."] = "নির্বাচিত ডেটাবেস ব্যাকআপটি খালি।",
            ["The selected file is not a PosApp database backup."] = "নির্বাচিত ফাইলটি PosApp ডেটাবেস ব্যাকআপ নয়।",
            ["The selected file is not a healthy SQLite backup."] = "নির্বাচিত ফাইলটি সুস্থ SQLite ব্যাকআপ নয়।",
            ["The selected supplier is inactive."] = "নির্বাচিত সরবরাহকারী নিষ্ক্রিয়।",
            ["The selected user could not be found."] = "নির্বাচিত ব্যবহারকারীকে পাওয়া যায়নি।",
            ["This product is not stock-tracked"] = "এই পণ্যটির স্টক অনুসরণ করা হয় না",
            ["This register session is already closed."] = "এই রেজিস্টার সেশনটি ইতিমধ্যে বন্ধ।",
            ["This sale belongs to a closed register. Process a refund in an open register instead of voiding it."] = "এই বিক্রয়টি বন্ধ রেজিস্টারের। এটি বাতিল না করে খোলা রেজিস্টারে ফেরত প্রক্রিয়া করুন।",
            ["This sale has already been refunded."] = "এই বিক্রয়টি ইতিমধ্যে ফেরত দেওয়া হয়েছে।",
            ["Used count cannot be negative."] = "ব্যবহারের সংখ্যা ঋণাত্মক হতে পারবে না।",
            ["User not found."] = "ব্যবহারকারী পাওয়া যায়নি।",
            ["Username must contain 3 to 60 letters, numbers, dots, dashes, or underscores."] = "ব্যবহারকারী নামে ৩ থেকে ৬০টি অক্ষর, সংখ্যা, ডট, ড্যাশ বা আন্ডারস্কোর থাকতে হবে।",
            ["Windows did not start the setup installer."] = "Windows সেটআপ ইনস্টলার চালু করেনি।",
            ["No active store is available."] = "কোনো সক্রিয় স্টোর পাওয়া যায়নি।",
            ["The selected store no longer exists."] = "নির্বাচিত স্টোরটি আর নেই।",
            ["That store code is already in use."] = "এই স্টোর কোডটি ইতিমধ্যে ব্যবহৃত হচ্ছে।",
            ["Store not found."] = "স্টোর পাওয়া যায়নি।",
            ["Switch to another store before deactivating the current store."] = "বর্তমান স্টোর নিষ্ক্রিয় করার আগে অন্য স্টোরে যান।",
            ["At least one store must remain active."] = "অন্তত একটি স্টোর সক্রিয় রাখতে হবে।",
            ["The selected store is inactive."] = "নির্বাচিত স্টোরটি নিষ্ক্রিয়।",
            ["The current store must have an active administrator before another store can be created."] = "আরেকটি স্টোর তৈরির আগে বর্তমান স্টোরে একজন সক্রিয় অ্যাডমিনিস্ট্রেটর থাকতে হবে।",
            ["A record cannot be moved between stores."] = "একটি রেকর্ড এক স্টোর থেকে অন্য স্টোরে সরানো যাবে না।",
            ["Store code is required."] = "স্টোর কোড আবশ্যক।",
            ["Store code cannot exceed 24 characters."] = "স্টোর কোড ২৪ অক্ষরের বেশি হতে পারবে না।",
            ["Store name cannot exceed 100 characters."] = "স্টোরের নাম ১০০ অক্ষরের বেশি হতে পারবে না।",
            ["Address cannot exceed 500 characters."] = "ঠিকানা ৫০০ অক্ষরের বেশি হতে পারবে না।",
            ["Phone cannot exceed 30 characters."] = "ফোন ৩০ অক্ষরের বেশি হতে পারবে না।"
        };

    public static string Translate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var bengali = LocalizationManager.Instance.IsBengali;
        var maps = Maps.Value;
        var exact = bengali ? maps.EnglishToBengali : maps.BengaliToEnglish;
        if (exact.TryGetValue(value, out var translated)) return translated;

        return bengali ? TranslateBengaliPattern(value) : value;
    }

    public static void LocalizeWindow(Window window)
    {
        var visited = new HashSet<DependencyObject>();
        LocalizeObject(window, visited);
    }

    private static void LocalizeObject(DependencyObject item, ISet<DependencyObject> visited)
    {
        if (!visited.Add(item)) return;

        TranslateLocalString(item, ToolTipService.ToolTipProperty);
        if (item is Window window) TranslateLocalString(window, Window.TitleProperty);
        if (item is TextBlock text) TranslateLocalString(text, TextBlock.TextProperty);
        if (item is ContentControl content) TranslateLocalString(content, ContentControl.ContentProperty);
        if (item is HeaderedContentControl headeredContent)
            TranslateLocalString(headeredContent, HeaderedContentControl.HeaderProperty);
        if (item is HeaderedItemsControl headeredItems)
            TranslateLocalString(headeredItems, HeaderedItemsControl.HeaderProperty);
        if (item is DataGrid grid)
        {
            foreach (var column in grid.Columns)
                TranslateLocalString(column, DataGridColumn.HeaderProperty);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
            LocalizeObject(child, visited);

        if (item is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
                LocalizeObject(VisualTreeHelper.GetChild(item, index), visited);
        }
    }

    private static void TranslateLocalString(DependencyObject item, DependencyProperty property)
    {
        // A literal assigned in code/XAML is a string. Bindings and DynamicResource
        // references are expressions; leave those intact so later switches continue
        // to update automatically.
        if (item.ReadLocalValue(property) is not string current) return;
        var translated = Translate(current);
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            item.SetValue(property, translated);
    }

    private static TranslationMaps BuildMaps()
    {
        var enToBn = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var english = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PosApp.Localization;component/Strings.en.xaml")
            };
            var bengali = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/PosApp.Localization;component/Strings.bn.xaml")
            };
            foreach (DictionaryEntry entry in english)
            {
                if (entry.Value is string source && bengali.Contains(entry.Key) &&
                    bengali[entry.Key] is string target &&
                    !string.Equals(source, target, StringComparison.Ordinal))
                    enToBn.TryAdd(source, target);
            }
        }
        catch
        {
            // Supplemental translations still cover code-generated validation and
            // error dialogs if a resource dictionary cannot be loaded during startup.
        }

        foreach (var item in SupplementalBengali) enToBn[item.Key] = item.Value;
        var bnToEn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in enToBn) bnToEn.TryAdd(item.Value, item.Key);
        return new TranslationMaps(enToBn, bnToEn);
    }

    private static string TranslateBengaliPattern(string value)
    {
        var match = Regex.Match(value, @"^Insufficient stock for (.+)\.$");
        if (match.Success) return $"{match.Groups[1].Value}-এর পর্যাপ্ত স্টক নেই।";
        match = Regex.Match(value, @"^Product not found or inactive: (.+)$");
        if (match.Success) return $"পণ্যটি পাওয়া যায়নি বা নিষ্ক্রিয়: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Discounts are disabled for (.+)\.$");
        if (match.Success) return $"{match.Groups[1].Value}-এর জন্য ছাড় বন্ধ আছে।";
        match = Regex.Match(value,
            @"^Sale completed\. Receipt: ([^\r\n]+)\r?\nReceived: ([^\r\n]+)\r?\nChange: ([^\r\n]+)(?:\r?\n\r?\nThe receipt is available in Sales History if you want to print it\.)?$",
            RegexOptions.Singleline);
        if (match.Success)
            return $"বিক্রয় সম্পন্ন হয়েছে। রসিদ: {match.Groups[1].Value}\n" +
                   $"গ্রহণ: {match.Groups[2].Value}\nফেরত: {match.Groups[3].Value}\n\n" +
                   "প্রয়োজন হলে বিক্রয় ইতিহাস থেকে রসিদ প্রিন্ট করুন।";
        match = Regex.Match(value, @"^Sale completed\. Receipt: (.+)$", RegexOptions.Singleline);
        if (match.Success) return $"বিক্রয় সম্পন্ন হয়েছে। রসিদ: {match.Groups[1].Value}";
        match = Regex.Match(value, @"^Refund sale (.+) for (.+)\?$");
        if (match.Success) return $"বিক্রয় {match.Groups[1].Value}, {match.Groups[2].Value} ফেরত দেবেন?";
        match = Regex.Match(value, @"^Refund processed\. Receipt: ([^\r\n]+)\r?\nAmount: (.+)$");
        if (match.Success) return $"ফেরত সম্পন্ন হয়েছে। রসিদ: {match.Groups[1].Value}\nপরিমাণ: {match.Groups[2].Value}";
        match = Regex.Match(value, @"^Void sale (.+)\? Stock will be returned\.$");
        if (match.Success) return $"বিক্রয় {match.Groups[1].Value} বাতিল করবেন? স্টক ফেরত যোগ হবে।";
        match = Regex.Match(value, @"^Delete user '(.+)'\?$");
        if (match.Success) return $"ব্যবহারকারী '{match.Groups[1].Value}' মুছবেন?";
        match = Regex.Match(value, @"^Deactivate (.+)\?$");
        if (match.Success) return $"{match.Groups[1].Value} নিষ্ক্রিয় করবেন?";
        match = Regex.Match(value, @"^Switch to (.+)\? You will return to the login screen\.$");
        if (match.Success) return $"{match.Groups[1].Value} স্টোরে যাবেন? আপনাকে লগইন স্ক্রিনে ফেরত নেওয়া হবে।";
        match = Regex.Match(value, @"^Purchase (.+) posted and stock updated\.$");
        if (match.Success) return $"ক্রয় {match.Groups[1].Value} পোস্ট হয়েছে এবং স্টক আপডেট হয়েছে।";
        match = Regex.Match(value, @"^Backup created successfully\.\s*(.+)$", RegexOptions.Singleline);
        if (match.Success) return $"ব্যাকআপ সফলভাবে তৈরি হয়েছে।\n\n{match.Groups[1].Value}";
        match = Regex.Match(value, @"^Exported (\d+) sales to:\s*(.+)$", RegexOptions.Singleline);
        if (match.Success) return $"{match.Groups[1].Value}টি বিক্রয় এখানে রপ্তানি হয়েছে:\n{match.Groups[2].Value}";
        match = Regex.Match(value, @"^Enter a value of at least (.+)\.$");
        if (match.Success) return $"অন্তত {match.Groups[1].Value} মান লিখুন।";
        match = Regex.Match(value, @"^Uploaded (\d+) store snapshot\(s\) containing ([\d,]+) rows\. Automatic incremental synchronization is active\.$");
        if (match.Success) return $"{match.Groups[1].Value}টি স্টোর স্ন্যাপশট আপলোড হয়েছে, মোট {match.Groups[2].Value}টি সারি। স্বয়ংক্রিয় ইনক্রিমেন্টাল সিঙ্ক চালু আছে।";
        match = Regex.Match(value, @"^Synchronized (\d+) store\(s\): ([\d,]+) pushed, ([\d,]+) pulled, ([\d,]+) conflicts\.$");
        if (match.Success) return $"{match.Groups[1].Value}টি স্টোর সিঙ্ক হয়েছে: {match.Groups[2].Value}টি পাঠানো, {match.Groups[3].Value}টি আনা, {match.Groups[4].Value}টি দ্বন্দ্ব।";
        match = Regex.Match(value, @"^Restored (\d+) store\(s\) and ([\d,]+) rows\. PosApp will now close; reopen it to continue\.$");
        if (match.Success) return $"{match.Groups[1].Value}টি স্টোর ও {match.Groups[2].Value}টি সারি পুনরুদ্ধার হয়েছে। PosApp এখন বন্ধ হবে; চালিয়ে যেতে আবার খুলুন।";
        match = Regex.Match(value, @"^Cloud contains (\d+) store\(s\) that are not on this device\. Use Restore Cloud Data to download the complete multi-store baseline\.$");
        if (match.Success) return $"ক্লাউডে {match.Groups[1].Value}টি স্টোর আছে যা এই ডিভাইসে নেই। সম্পূর্ণ মাল্টি-স্টোর ভিত্তি ডাউনলোড করতে ক্লাউড ডেটা পুনরুদ্ধার ব্যবহার করুন।";
        match = Regex.Match(value, @"^Unable to (.+)$");
        if (match.Success) return "কাজটি সম্পন্ন করা যায়নি";
        match = Regex.Match(value, @"^Cannot (.+)$");
        if (match.Success) return "কাজটি করা যায়নি";
        return value;
    }
}

public static class LocalizedMessageBox
{
    public static MessageBoxResult Show(string message)
        => System.Windows.MessageBox.Show(RuntimeUiText.Translate(message));

    public static MessageBoxResult Show(string message, string caption)
        => System.Windows.MessageBox.Show(RuntimeUiText.Translate(message), RuntimeUiText.Translate(caption));

    public static MessageBoxResult Show(
        string message, string caption, MessageBoxButton buttons)
        => System.Windows.MessageBox.Show(
            RuntimeUiText.Translate(message), RuntimeUiText.Translate(caption), buttons);

    public static MessageBoxResult Show(
        string message, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        => System.Windows.MessageBox.Show(
            RuntimeUiText.Translate(message), RuntimeUiText.Translate(caption), buttons, icon);

    public static MessageBoxResult Show(
        Window owner, string message, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        => System.Windows.MessageBox.Show(
            owner, RuntimeUiText.Translate(message), RuntimeUiText.Translate(caption), buttons, icon);
}
