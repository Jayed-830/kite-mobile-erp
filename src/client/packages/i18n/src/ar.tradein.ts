// Arabic strings for Phase 7 (trade-ins, store credit, the profit margin scheme): the same keys as en.tradein.ts.
import type { tradeinEn } from './en.tradein';

export const tradeinAr: typeof tradeinEn = {
  tradein: {
    title: 'الاستبدال', one: 'استبدال', start: 'استبدال (F6)', number: 'رقم الاستبدال', reportTab: 'القيمة مقابل إعادة البيع', listTab: 'عمليات الاستبدال',
    statuses: { open: 'مفتوح', accepted: 'مقبول', settled: 'مسوّى', rejected: 'مرفوض', cancelled: 'ملغى', unsettled: 'لم يُسوَّ بعد', all: 'الكل' },
    kinds: { sale: 'مخصوم من البيع', store_credit: 'رصيد لدى المتجر', cash: 'مدفوع نقدًا' },
    steps: { customer: 'العميل', device: 'الجهاز', inspection: 'الفحص', valuation: 'القيمة', consent: 'الموافقة', settlement: 'التسوية', done: 'تم' },
    customer: {
      intro: 'من يبيع الجهاز؟ يحتاج الاستبدال إلى اسم العميل ورقم هاتفه.', search: 'ابحث عن العميل (الاسم أو الهاتف)', new: 'عميل جديد',
      civilId: 'الرقم المدني', civilIdHelp: 'يُحفظ مشفرًا؛ ويرى الموظفون آخر أربعة أرقام فقط.', civilIdRequired: 'تسجّل المنشأة الرقم المدني لكل من تشتري منه جهازًا.',
      sellerVat: 'الرقم الضريبي للبائع (فقط إذا كان البائع منشأة مسجلة في ضريبة القيمة المضافة)', sellerVatHelp: 'الشراء من بائع مسجل في الضريبة لا يخضع لنظام هامش الربح.',
      noPhone: 'لا يوجد رقم هاتف لهذا العميل. أضفه قبل استلام الجهاز.', chosen: 'العميل البائع',
    },
    device: {
      model: 'الطراز', modelSearch: 'ابحث عن الطراز (الاسم أو رمز الصنف)', imei: 'رقم IMEI (امسحه من الجهاز أو ‎*#06#‎)', serial: 'الرقم التسلسلي', storage: 'السعة', colour: 'اللون',
      check: 'افحص الجهاز', checking: 'جارٍ التحقق من رقم IMEI…', headOffice: 'تم التحقق لدى المركز الرئيسي وفي قائمة الحظر.', headOfficeOffline: 'تعذّر الوصول إلى المركز الرئيسي: تم التحقق في هذا الفرع فقط.',
      reconnect: 'هذا الهاتف باعته المنشأة من قبل: يعود إلى سجله نفسه.', pickModel: 'اختر الطراز أولًا.',
    },
    inspection: {
      intro: 'افحص الجهاز أمام العميل. البنود غير السليمة تقترح خصومات.', cloudLock: 'أزال العميل حسابه السحابي (Apple ID / Google) وقفل التفعيل أمامي',
      cloudLockHelp: 'لا يمكن إعادة بيع جهاز مقفل: لا يُقبل أبدًا وهو مقفل.', battery: 'صحة البطارية (%)', grade: 'الدرجة', condition: 'الحالة',
      conditions: { used: 'مستعمل', damaged: 'به ضرر' }, deductions: 'الخصومات', suggested: 'مقترح من الفحص', noGuide: 'لا توجد قيمة في الدليل',
    },
    valuation: {
      guide: 'قيمة الدليل', deductions: 'الخصومات', offer: 'العرض', agreed: 'القيمة المتفق عليها', agreedHelp: 'ما يقبله العميل مقابل الجهاز.',
      approval: 'هذه القيمة تحتاج موافقة المدير: فهي أعلى من حدك البالغ {limit} أو تزيد عن العرض بأكثر من {percent}%.', approvalNoGuide: 'لا توجد قيمة في الدليل لهذا الطراز والدرجة: يعتمد المدير القيمة.',
      approved: 'وافق المدير.', getApproval: 'اطلب الموافقة', recalc: 'احسب العرض',
    },
    consent: {
      intro: 'يؤكد العميل الإقرار أدناه.', method: 'كيف يؤكد العميل؟', methods: { on_screen: 'يكتب اسمه على الشاشة', paper: 'يوقّع النموذج الورقي' },
      name: 'يكتب العميل اسمه الكامل', reference: 'رقم النموذج الورقي', declaration: 'الإقرار',
    },
    settlement: {
      intro: 'كيف تُسوّى القيمة؟', sale: 'استخدمها للدفع في هذا البيع', saleHelp: 'تصبح القيمة دفعة في نقطة البيع؛ وما يتبقى يُسوّى بعد البيع.',
      saleEmpty: 'السلة فارغة الآن. يمكن استخدامها لاحقًا: تبقى على العميل كعملية استبدال مفتوحة.', storeCredit: 'رصيد لدى المتجر',
      storeCreditHelp: 'يستطيع العميل استخدامه في أي فرع.', cash: 'ادفع نقدًا من الدرج', cashHelp: 'شراء مباشر: يدفع الدرج القيمة المتفق عليها.', accept: 'اقبل الجهاز',
    },
    done: {
      title: 'تم قبول الاستبدال {number}', unit: 'الجهاز في المخزون قيد الفحص.', selfInvoice: 'فاتورة ذاتية بنظام هامش الربح {number}', document: 'اطبع المستند',
      inCart: 'قيمة الاستبدال جاهزة كدفعة لهذا البيع.', storeCredit: 'أُضيف {amount} إلى رصيد العميل لدى المتجر.', cash: 'ادفع {amount} للعميل من الدرج.',
    },
    pay: {
      tradeIns: 'رصيد الاستبدال', storeCredit: 'رصيد المتجر', balance: 'الرصيد {amount}', use: 'استخدم', useAll: 'استخدم الكل', needCustomer: 'اختر العميل لاستخدام عمليات الاستبدال أو رصيد المتجر.',
      left: 'متبقٍ {amount}',
    },
    rest: {
      title: 'قيمة متبقية من الاستبدال', help: 'تبقّى من {number} مبلغ {amount} بعد هذا البيع. سوّه الآن:', storeCredit: 'كرصيد لدى المتجر', cash: 'نقدًا', later: 'لاحقًا',
      done: 'تمت التسوية.',
    },
    detail: {
      device: 'الجهاز', seller: 'البائع', values: 'القيم', inspection: 'الفحص', settlements: 'التسويات', outstanding: 'المتبقي للتسوية', consent: 'الموافقة',
      reveal: 'اعرض الرقم المدني', revealed: 'الرقم المدني {value} (هذا العرض مسجل).', reject: 'ارفض', rejectReason: 'لماذا يُرفض؟', cancel: 'ألغِ الاستبدال',
      settle: 'سوِّ المتبقي كرصيد لدى المتجر', margin: 'نظام هامش الربح', marginYes: 'تم الشراء بنظام هامش الربح (فاتورة ذاتية {number})', marginNo: 'شراء عادي (ضريبة أساسية عند إعادة البيع)',
      reconnected: 'عاد إلى سجل الجهاز الذي باعته المنشأة', acceptedBy: 'قُبل {when} بواسطة {who}', unit: 'افتح الجهاز', document: 'المستند (PDF)',
      cashAtTill: 'الدفع النقدي يتم في نقطة البيع.', paperForm: 'النموذج الورقي {reference}', onScreen: 'الاسم المكتوب: {name}',
    },
    list: { customer: 'العميل', device: 'الجهاز', agreed: 'المتفق عليه', left: 'المتبقي', when: 'التاريخ', search: 'ابحث بالرقم أو العميل أو الهاتف أو رقم IMEI', empty: 'لا توجد عمليات استبدال.' },
    report: {
      intro: 'تكلفة كل جهاز مستبدل وما أُنفق عليه وسعر بيعه.', agreed: 'سعر الشراء', added: 'ما أُنفق عليه', resale: 'سعر البيع (الصافي)', resaleTax: 'الضريبة', profit: 'الربح',
      notSold: 'لم يُبع بعد', sale: 'البيع',
    },
    settings: {
      title: 'إعدادات الاستبدال', limits: 'الحدود', staffLimit: 'يقبل الموظفون حتى (ر.ع.)', overGuide: 'وحتى هذه النسبة % فوق عرض الدليل', requireCivilId: 'سجّل الرقم المدني للبائع',
      declaration: 'إقرار البائع', declarationEn: 'الإقرار (بالإنجليزية)', declarationAr: 'الإقرار (بالعربية)', saved: 'تم الحفظ.',
      guide: 'دليل الأسعار', guideHelp: 'ما تدفعه المنشأة مقابل الطراز في كل درجة قبل الخصومات. اتركه فارغًا لحذف القيمة من الدليل.', guideModel: 'الطراز', saveGuide: 'احفظ الدليل',
      deductionsTitle: 'الخصومات', code: 'الرمز', nameEn: 'الاسم (بالإنجليزية)', nameAr: 'الاسم (بالعربية)', kind: 'النوع', kinds: { amount: 'مبلغ (ر.ع.)', percent: '% من الدليل' }, value: 'القيمة',
      checklist: 'يقترحه بند الفحص', none: 'لا شيء', add: 'أضف خصمًا', active: 'نشط',
    },
    margin: {
      title: 'نظام هامش الربح', help: 'يمكن بيع الأجهزة المستعملة المشتراة من أشخاص غير مسجلين في ضريبة القيمة المضافة بضريبة على هامش الربح فقط، بعد موافقة جهاز الضرائب. سجّل الموافقة هنا؛ ويتبعها رمز VAT-MARGIN.',
      approved: 'وافق جهاز الضرائب على نظام هامش الربح', reference: 'رقم الموافقة', approvedOn: 'تاريخ الموافقة', note: 'ملاحظة', status: 'رمز VAT-MARGIN {status}.',
      statuses: { active: 'مفعّل', inactive: 'متوقف' }, withdraw: 'اسحب الموافقة', saved: 'تم الحفظ.',
    },
    storeCredit: { title: 'رصيد المتجر', balance: 'الرصيد', none: 'لا يوجد رصيد لدى المتجر بعد.', view: 'رصيد المتجر' },
    figures: { tradeInCredit: 'رصيد الاستبدال المستخدم', storeCreditPayments: 'رصيد المتجر المستخدم', tradeInPayouts: 'مدفوعات الاستبدال نقدًا' },
  },
};
