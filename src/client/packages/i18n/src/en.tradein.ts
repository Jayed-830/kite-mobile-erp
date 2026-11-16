// English strings for Phase 7 (trade-ins, store credit, the profit margin scheme). ar.tradein.ts must have the same keys.
export const tradeinEn = {
  tradein: {
    title: 'Trade-ins', one: 'Trade-in', start: 'Trade-in (F6)', number: 'Trade-in number', reportTab: 'Value against resale', listTab: 'Trade-ins',
    statuses: { open: 'Open', accepted: 'Accepted', settled: 'Settled', rejected: 'Rejected', cancelled: 'Cancelled', unsettled: 'Not settled yet', all: 'All' },
    kinds: { sale: 'Applied to sale', store_credit: 'Store credit', cash: 'Paid in cash' },
    steps: { customer: 'Customer', device: 'Device', inspection: 'Inspection', valuation: 'Value', consent: 'Consent', settlement: 'Settlement', done: 'Done' },
    customer: {
      intro: 'Who is selling the device? A trade-in needs the customer’s name and phone.', search: 'Find the customer (name or phone)', new: 'New customer',
      civilId: 'Civil ID', civilIdHelp: 'Kept encrypted; staff see only the last four digits.', civilIdRequired: 'The business records the civil ID of everyone it buys a device from.',
      sellerVat: 'Seller’s VAT number (only if the seller is a VAT-registered business)', sellerVatHelp: 'A purchase from a VAT-registered seller is not under the profit margin scheme.',
      noPhone: 'This customer has no phone number. Add it before taking a device in.', chosen: 'Selling customer',
    },
    device: {
      model: 'Model', modelSearch: 'Search the model (name or SKU)', imei: 'IMEI (scan it from the device or *#06#)', serial: 'Serial number', storage: 'Storage', colour: 'Colour',
      check: 'Check the device', checking: 'Checking the IMEI…', headOffice: 'Checked with head office and the blocked list.', headOfficeOffline: 'Head office could not be reached: checked in this branch only.',
      reconnect: 'This phone was sold by the business before: it comes back to its own record.', pickModel: 'Choose the model first.',
    },
    inspection: {
      intro: 'Check the device in front of the customer. Failed items suggest deductions.', cloudLock: 'The customer removed their cloud account (Apple ID / Google) and the activation lock in front of me',
      cloudLockHelp: 'A locked device cannot be resold: it is never accepted while locked.', battery: 'Battery health (%)', grade: 'Grade', condition: 'Condition',
      conditions: { used: 'Used', damaged: 'Damaged' }, deductions: 'Deductions', suggested: 'suggested by the inspection', noGuide: 'no guide value',
    },
    valuation: {
      guide: 'Guide value', deductions: 'Deductions', offer: 'Offer', agreed: 'Agreed value', agreedHelp: 'What the customer accepts for the device.',
      approval: 'This value needs a manager’s approval: it is above your limit of {limit} or more than {percent}% over the offer.', approvalNoGuide: 'There is no guide value for this model and grade: a manager approves the value.',
      approved: 'Approved by a manager.', getApproval: 'Get approval', recalc: 'Work out the offer',
    },
    consent: {
      intro: 'The customer confirms the declaration below.', method: 'How does the customer confirm?', methods: { on_screen: 'Types their name on screen', paper: 'Signs the paper form' },
      name: 'Customer types their full name', reference: 'Paper form number', declaration: 'Declaration',
    },
    settlement: {
      intro: 'How is the value settled?', sale: 'Use it to pay for this sale', saleHelp: 'The value becomes a payment at the till; anything left over is settled after the sale.',
      saleEmpty: 'There is nothing in the cart yet. You can still use it later: it stays on the customer as an open trade-in.', storeCredit: 'Store credit',
      storeCreditHelp: 'The customer can spend it at any branch.', cash: 'Pay cash from the drawer', cashHelp: 'A buy-back: the drawer pays the agreed value.', accept: 'Accept the device',
    },
    done: {
      title: 'Trade-in {number} accepted', unit: 'The device is in stock under inspection.', selfInvoice: 'Profit margin self-invoice {number}', document: 'Print the document',
      inCart: 'The trade-in value is ready as a payment for this sale.', storeCredit: '{amount} added to the customer’s store credit.', cash: 'Pay {amount} to the customer from the drawer.',
    },
    pay: {
      tradeIns: 'Trade-in credit', storeCredit: 'Store credit', balance: 'Balance {amount}', use: 'Use', useAll: 'Use all', needCustomer: 'Choose the customer to use their trade-ins or store credit.',
      left: '{amount} left on it',
    },
    rest: {
      title: 'Value left on the trade-in', help: '{number} has {amount} left after this sale. Settle it now:', storeCredit: 'As store credit', cash: 'In cash', later: 'Later',
      done: 'Settled.',
    },
    detail: {
      device: 'Device', seller: 'Seller', values: 'Values', inspection: 'Inspection', settlements: 'Settlements', outstanding: 'Left to settle', consent: 'Consent',
      reveal: 'Show the civil ID', revealed: 'Civil ID {value} (this view is recorded).', reject: 'Reject', rejectReason: 'Why is it rejected?', cancel: 'Cancel the trade-in',
      settle: 'Settle the rest as store credit', margin: 'Profit margin scheme', marginYes: 'Bought under the profit margin scheme (self-invoice {number})', marginNo: 'Ordinary purchase (standard VAT on resale)',
      reconnected: 'Came back to the device record the business sold', acceptedBy: 'Accepted {when} by {who}', unit: 'Open the device', document: 'Document (PDF)',
      cashAtTill: 'Paying out cash is done at the till.', paperForm: 'Paper form {reference}', onScreen: 'Typed name: {name}',
    },
    list: { customer: 'Customer', device: 'Device', agreed: 'Agreed', left: 'Left', when: 'When', search: 'Search number, customer, phone or IMEI', empty: 'No trade-ins.' },
    report: {
      intro: 'What each traded-in device cost, what was spent on it and what it sold for.', agreed: 'Bought for', added: 'Spent on it', resale: 'Sold for (net)', resaleTax: 'VAT', profit: 'Profit',
      notSold: 'Not sold yet', sale: 'Sale',
    },
    settings: {
      title: 'Trade-in settings', limits: 'Limits', staffLimit: 'Staff can accept up to (OMR)', overGuide: 'And up to this % over the guide offer', requireCivilId: 'Record the seller’s civil ID',
      declaration: 'Seller’s declaration', declarationEn: 'Declaration (English)', declarationAr: 'Declaration (Arabic)', saved: 'Saved.',
      guide: 'Price guide', guideHelp: 'What the business pays for a model in each grade before deductions. Leave empty to take a value off the guide.', guideModel: 'Model', saveGuide: 'Save the guide',
      deductionsTitle: 'Deductions', code: 'Code', nameEn: 'Name (English)', nameAr: 'Name (Arabic)', kind: 'Kind', kinds: { amount: 'Amount (OMR)', percent: '% of guide' }, value: 'Value',
      checklist: 'Suggested by checklist item', none: 'None', add: 'Add a deduction', active: 'Active',
    },
    margin: {
      title: 'Profit margin scheme', help: 'Second-hand devices bought from people not registered for VAT can be sold with VAT on the profit margin only, once the Tax Authority has approved it. Record the approval here; VAT-MARGIN follows it.',
      approved: 'The Tax Authority approved the profit margin scheme', reference: 'Approval reference', approvedOn: 'Approved on', note: 'Note', status: 'VAT-MARGIN is {status}.',
      statuses: { active: 'active', inactive: 'switched off' }, withdraw: 'Withdraw the approval', saved: 'Saved.',
    },
    storeCredit: { title: 'Store credit', balance: 'Balance', none: 'No store credit yet.', view: 'Store credit' },
    figures: { tradeInCredit: 'Trade-in credit taken', storeCreditPayments: 'Store credit taken', tradeInPayouts: 'Trade-ins paid out' },
  },
};
