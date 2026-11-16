// English strings for Phase 6 (IMEI and serial number management). ar.imei.ts must have the same keys.
export const imeiEn = {
  imei: {
    central: {
      verified: 'Confirmed by head office', pending: 'Head office check pending', conflict: 'IMEI conflict', filter: 'Head office check', any: 'Any',
      headOffice: 'This server is head office: every IMEI is checked across all branches here.',
      reachable: 'Head office is reachable. Last contact {when}.', unreachable: 'Head office cannot be reached now. New devices are checked in this branch and confirmed by head office later.',
      pendingCount: '{count} devices are waiting for head office to confirm their IMEIs.', conflictCount: '{count} devices are on hold because of an IMEI conflict.',
      openConflicts: '{count} IMEI conflicts are open.', strict: 'Devices waiting for head office are kept off sale until confirmed.', pushError: 'The last send to head office failed: {error}',
      never: 'never',
    },
    holds: {
      title: 'Holds', reasons: { manager: 'Manager hold', imei_conflict: 'IMEI conflict', blocked_imei: 'Blocked IMEI' }, open: 'On hold', released: 'Released {when}',
      releaseNote: 'Release note', heldOn: 'Held {when}',
      explain: { imei_conflict: 'Head office found this IMEI on another device: correct the IMEI or reverse the receipt.', blocked_imei: 'This IMEI is on the blocked list at head office.' },
    },
    identifiers: {
      title: 'IMEIs and serial numbers', kinds: { imei: 'IMEI', serial: 'Serial number' }, active: 'In use', retired: 'Replaced', entries: { scan: 'Scanned', manual: 'Typed' },
      correct: 'Correct the IMEI', correctHelp: 'Use this when the IMEI was scanned or typed wrongly. The old IMEI stays in the history and cannot be used again for this device.',
      newImei: 'Correct IMEI', newImei2: 'Correct IMEI 2', newSerial: 'Correct serial number', reason: 'Why is it corrected?', corrected: 'The IMEI is corrected.',
      reverse: 'Reverse the receipt', reverseHelp: 'Use this when the device was never really received here, for example when the same phone was received twice. It leaves stock at its cost and its IMEI is freed.',
      reverseReason: 'Why is the receipt reversed?', reversedOn: 'The receipt is reversed on {number}.',
    },
    check: {
      checking: 'Checking with head office…', ok: 'OK', localOnly: 'Head office not reachable: checked in this branch only', hint: 'Model hint',
      pendingNotice: '{count} devices were received while head office could not be reached. They are confirmed as soon as head office answers.',
      blockedNotice: 'Remove the devices with a problem before saving.',
    },
    conflicts: {
      help: 'Head office found the same IMEI on more than one device. Fix it where the device is: correct a mistyped IMEI, or reverse the receipt of the device that was never really there. The conflict closes by itself and the devices are released.',
      branchHelp: 'Devices in this branch held because head office found their IMEI on another device. Open each one to correct its IMEI or reverse its receipt.',
      empty: 'No IMEI conflicts.', open: 'Open', resolved: 'Resolved', all: 'All', value: 'IMEI', sides: 'Registered at', opened: 'Opened', resolution: 'How it was settled',
      outcomes: { kept: 'Kept', corrected: 'IMEI corrected', reversed: 'Receipt reversed' }, thisServer: 'here', openDevice: 'Open the device', heldHere: 'Held here',
    },
    emergency: {
      title: 'Phones sold in emergency mode to settle', help: 'These phones were sold while the till worked offline, but the same phone was sold or moved before the sale reached the server. Settle each sale with the phone the customer actually took, or close it with a note.',
      empty: 'Nothing to settle.', phone: 'Phone on the sale', substitute: 'Phone the customer took', pick: 'Choose a phone of the same model', apply: 'Record the sale', dismiss: 'Close with a note',
      note: 'Note', noteRequired: 'Say why the sale is closed without recording it.', settled: 'Settled', outcomes: { applied: 'Recorded on {number}', dismissed: 'Closed' }, settle: 'Settle',
      sale: 'Sale', total: 'Total',
    },
    blocked: {
      help: 'The blocked list is kept at head office and copied to every branch. A blocked IMEI is refused at receiving, and a device already in stock is put on hold.',
      branchHelp: 'This list comes from head office. Ask head office to add or remove an IMEI.', add: 'Add to the blocked list', value: 'IMEI', reason: 'Reason', reasonHint: 'For example: reported stolen',
      reference: 'Police or report reference', addedBy: 'Added by', addedAt: 'Added', remove: 'Take off the list', removeReason: 'Why is it taken off the list?', empty: 'No blocked IMEIs.',
      statuses: { active: 'On the list', removed: 'Taken off' }, devicesHere: 'Devices here', added: '{value} is on the blocked list.', search: 'Search IMEI or reason',
    },
    lookup: {
      title: 'Look up an IMEI', hint: 'Scan or type an IMEI or serial number to see its history in every branch.', go: 'Look up', here: 'In this branch', headOffice: 'Registered at head office',
      unreachable: 'Head office cannot be reached: showing this branch only.', blocked: 'On the blocked list: {reason}', model: 'Model: {model}', none: 'This IMEI was never received in this business.',
      soldOn: 'Sold on {number}', replaced: 'Old IMEI (replaced)', conflictRow: 'In conflict', openConflicts: 'Open IMEI conflicts: {count}',
    },
    counts: {
      start: 'Start a count', allModels: 'All devices', model: 'Model', note: 'Note', number: 'Number', started: 'Started', by: 'By', scans: 'Scans', back: 'All counts',
      statuses: { open: 'Open', posted: 'Finished', cancelled: 'Cancelled' }, empty: 'No counts yet.', title: 'Count {number}',
      scan: 'Scan each device', scanHelp: 'Scan every device on the shelves, in the drawers and in the window. One scan per device.', expected: 'Expected', found: 'Found', missing: 'Missing', extra: 'Not expected',
      results: { here: 'Counted', other_model: 'Another model', elsewhere: 'Belongs to another branch', not_in_stock: 'Not in stock in the books', unknown: 'Not a device of this business' },
      post: 'Finish the count', postHelp: 'Missing devices can be written off now, or left in stock for a manager to look into.', writeOffMissing: 'Write off the missing devices', reason: 'Reason for the write-off',
      posted: 'Count {number} finished: {found} of {expected} found.', cancel: 'Cancel the count', removeScan: 'Remove', openCount: 'A count is open', lastScan: 'Last scan',
    },
    settings: {
      strict: 'Keep devices off sale until head office confirms the IMEI', strictHelp: 'When off, a device received while head office cannot be reached can be sold straight away; head office checks it later.',
      tac: 'Model table (TAC)', tacHelp: 'Import a CSV file with three columns: TAC (the first 8 digits of the IMEI), brand, model. Receiving warns when an IMEI does not match the model chosen.',
      tacCount: '{count} models in the table', tacLast: 'Last import {when}', tacImport: 'Import the file', tacFile: 'CSV file', tacImported: '{imported} models imported, {skipped} lines skipped.',
      serialFormats: 'Serial number formats', serialHelp: 'A pattern for each brand (a regular expression, for example ^[A-Z0-9]{12}$). A serial number that does not match is refused at receiving.',
      brand: 'Brand', pattern: 'Pattern', noPattern: 'Any serial number', saved: 'Saved.',
    },
    till: { scanDevice: 'Scan the device’s IMEI', pending: 'Head office check pending', scanOrPick: 'Scan the IMEI of the phone in your hand, or choose it from the list.' },
  },
};
