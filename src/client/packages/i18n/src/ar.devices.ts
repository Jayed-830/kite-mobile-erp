// Arabic strings for Phase 5; the same keys as en.devices.ts.
import type { devicesEn } from './en.devices';

export const devicesAr: typeof devicesEn = {
  devices: {
    title: 'الأجهزة', tabs: { stock: 'مخزون الأجهزة', inspections: 'الفحص', refurbishment: 'التجديد', reports: 'التقارير', conflicts: 'تعارضات IMEI', blocked: 'أرقام IMEI المحظورة', counts: 'جرد الأجهزة' },
    searchHint: 'رقم IMEI أو الرقم التسلسلي أو رمز الصنف أو الاسم', allStatuses: 'كل الحالات', allConditions: 'كل الأوضاع', empty: 'لا توجد أجهزة بعد.', inShop: 'في المحل',
    imei: 'رقم IMEI', imei2: 'رقم IMEI الثاني', serial: 'الرقم التسلسلي', identifier: 'رقم IMEI / التسلسلي', item: 'الصنف', status: 'الحالة', condition: 'الوضع', grade: 'الدرجة',
    noGrade: 'بلا درجة', cost: 'التكلفة', price: 'السعر', age: 'العمر (أيام)', received: 'تاريخ الاستلام', branch: 'الفرع', number: 'الرقم', when: 'الوقت', none: 'لا يوجد',
    statuses: {
      in_stock: 'في المخزون', under_inspection: 'بانتظار الفحص', returned_inspection: 'مرتجع بانتظار الفحص', refurbishing: 'قيد التجديد',
      for_parts: 'للقطع', supplier_return: 'للإرجاع إلى المورد', sold: 'مباع', in_transit: 'قيد النقل', quarantined: 'موقوف', supplier_returned: 'أُرجع إلى المورد',
      written_off: 'مشطوب', reversed: 'أُلغي استلامه',
    },
    conditions: { brand_new: 'جديد', used: 'مستعمل', refurbished: 'مجدد', damaged: 'به ضرر', for_parts: 'للقطع' },
    back: 'كل الأجهزة', costTitle: 'تكلفة هذا الجهاز', acquisition: 'سعر الشراء', parts: 'القطع', labour: 'الأجور', external: 'أعمال خارجية', total: 'إجمالي التكلفة',
    priceTitle: 'سعر البيع', listPrice: 'يُباع بسعر', newPrice: 'السعر جديدًا', suggested: 'سعر الدرجة المقترح', ownPrice: 'سعر خاص', setPrice: 'تحديد السعر',
    priceHelp: 'اتركه فارغًا ليُباع بسعر الدرجة المقترح.', warrantyDays: 'الضمان (أيام)', noPrice: 'لا يوجد سعر بعد: حدد سعرًا أو درجة.',
    history: 'السجل', sales: 'مبيعات هذا الجهاز', profit: 'الربح', net: 'الصافي', inspections: 'عمليات الفحص', refurbishments: 'عمليات التجديد', document: 'المستند',
    inspect: 'فحص', openRefurb: 'فتح أمر التجديد', hold: 'إيقاف مؤقت', release: 'إفراج', writeOff: 'شطب', reason: 'السبب',
    writeOffHelp: 'يخرج الجهاز من المخزون بتكلفته إلى حساب شطب المخزون.', writtenOff: 'تم الشطب بالمستند {number}.',
    events: {
      received: 'استُلم', inspected: 'فُحص', cost_added: 'أُضيفت تكلفة', refurb_completed: 'اكتمل التجديد', sold: 'بيع', sale_voided: 'أُلغي البيع',
      returned: 'أرجعه العميل', written_off: 'شُطب', held: 'أُوقف مؤقتًا', released: 'أُفرج عنه', price_set: 'حُدد السعر', supplier_returned: 'أُرجع إلى المورد',
      transferred_out: 'أُرسل إلى فرع آخر', transferred_in: 'استُلم من فرع آخر', identifier_corrected: 'صُحّح رقم IMEI', receipt_reversed: 'أُلغي الاستلام',
    },
    inspectTitle: 'فحص {item}', checklist: 'قائمة الفحص', pass: 'سليم', fail: 'فاشل', suggestion: 'تقترح القائمة: {outcome}', failedItems: 'البنود الفاشلة: {items}',
    incomplete: 'أجب عن كل البنود لترى الاقتراح.', decision: 'القرار',
    outcomes: { restock: 'إعادة إلى المخزون', refurbish: 'تجديد', for_parts: 'تخصيص للقطع', supplier_return: 'إرجاع إلى المورد' },
    resultCondition: 'الوضع بعد الفحص', note: 'ملاحظة', noteRequired: 'اذكر سبب اختلاف قرارك عن القائمة.', save: 'حفظ الفحص',
    inspected: 'حُفظ الفحص {number}.', queueEmpty: 'لا يوجد جهاز بانتظار الفحص.', waitingSince: 'بانتظار منذ',
    refurbTitle: 'أمر التجديد {number}', problem: 'المشكلة', workDone: 'العمل المنجز', technician: 'الفني', unassigned: 'غير محدد', costs: 'التكاليف', addCost: 'إضافة تكلفة',
    kinds: { part: 'قطعة من المخزون', labour: 'أجور', external: 'عمل خارجي' }, part: 'القطعة', qty: 'الكمية', minutes: 'الدقائق', ratePerHour: 'أجر الساعة: {rate}',
    adds: 'يضيف {amount} إلى تكلفة الجهاز', vendor: 'المنفذ', reference: 'الفاتورة أو المرجع', amount: 'المبلغ', add: 'إضافة', reverse: 'إلغاء التكلفة', reversed: 'أُلغيت',
    reversal: 'إلغاء: {note}', reverseReason: 'لماذا تُلغى هذه التكلفة؟', complete: 'إكمال', completeTitle: 'إنهاء التجديد', comeBackAs: 'يعود الجهاز بوضع',
    costSoFar: 'تكلفة الجهاز حتى الآن', orderStatus: { open: 'مفتوح', completed: 'مكتمل' }, noOrders: 'لا توجد أوامر تجديد.', opened: 'تاريخ الفتح', noCosts: 'لا توجد تكاليف بعد.',
    stockReport: 'الأجهزة في المخزون حسب الوضع والدرجة', units: 'عدد الأجهزة', averageAge: 'متوسط العمر (أيام)', profitReport: 'الربح لكل جهاز مباع',
    refurbReport: 'تكلفة التجديد حسب الفني', orders: 'الأوامر', completedOrders: 'المكتملة',
    scanImeis: 'امسح أرقام IMEI', scanHelp: 'امسح رقم IMEI أو الرقم التسلسلي لكل جهاز. جهاز واحد في كل مسح.', scanned: 'تم مسح {count}', comesInAs: 'يدخل بوضع',
    readyToSell: 'جاهز للبيع', needsInspection: 'بانتظار الفحص', invalidImei: 'رقم IMEI غير صحيح', scannedTwice: 'مُسح مرتين', remove: 'إزالة',
    settings: {
      title: 'إعدادات الأجهزة', labourRate: 'أجر الساعة', reservationMinutes: 'تحجز السلة الجهاز لمدة (دقائق)', parkedMinutes: 'يحجز البيع المعلّق الجهاز لمدة (دقائق)',
      priceStep: 'تقريب الأسعار المقترحة إلى', grades: 'الدرجات', code: 'الرمز', nameEn: 'الاسم (بالإنجليزية)', nameAr: 'الاسم (بالعربية)', percent: 'النسبة من السعر جديدًا', newGrade: 'إضافة درجة',
      checklist: 'قائمة الفحص', itemCode: 'الرمز', labelEn: 'البند (بالإنجليزية)', labelAr: 'البند (بالعربية)', kind: 'نوع الإجابة', required: 'إلزامي', minPercent: 'أدنى نسبة',
      onFail: 'عند الفشل', addItem: 'إضافة بند', kinds: { pass_fail: 'سليم / فاشل', percent: 'نسبة مئوية', text: 'ملاحظة' },
      onFails: { refurbish: 'تجديد', damaged: 'به ضرر', for_parts: 'للقطع' }, active: 'مفعّل',
    },
    till: {
      pickUnit: 'اختر الجهاز', noUnits: 'لا يوجد جهاز من هذا الصنف في مخزون هذا الفرع.', reservedUntil: 'محجوز لك حتى {time}', damaged: 'به ضرر: أبلغ العميل',
      scanReturned: 'امسح الجهاز المرتجع', goesToInspection: 'يذهب إلى الفحص ولا يعود للبيع مباشرة', imeiHint: 'أو امسح رقم IMEI',
    },
  },
};
