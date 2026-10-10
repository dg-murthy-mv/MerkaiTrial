// =====================================================================
// FILE: MerkaiTrial.Domain/Entities/CustomField.cs
//
// NEW FILE (075 — custom fields, Round A).
// 076 — + ShowInList: the field appears as a column on the record's list
//       page (Round B). See the property.
// 079 — + MapToContactFieldId / MapToDealFieldId: on a LEAD field, which
//       Contact field and which Deal field receive its value when the
//       lead is converted. Null on every other kind of field. See the
//       properties.
//
// Two tables:
//
//   CustomFieldDefinitions  what a workspace has decided to record —
//                           "Renewal date", "Licence tier", "GST
//                           registered?" — scoped by tenant AND by the
//                           kind of record it belongs on (EntityType).
//
//   CustomFieldValues       one row per (field, record) that actually
//                           has a value. No row means "not filled in".
//
// ─────────────────────────────────────────────────────────────────────
// WHY A VALUES TABLE AND NOT A JSON COLUMN ON Contacts
//
//   Round B puts these fields on the list page with search and filter,
//   and that is where the choice gets tested. A JSON column turns every
//   filter into JSON_VALUE(...) over every row of the tenant — no index
//   can help, and "renewal date before 31 March" becomes a string
//   comparison on whatever text happens to be in the blob. A values
//   table with one TYPED column per kind gives EF a plain EXISTS
//   subquery, a real index per type, and dates that sort as dates.
//
//   The cost is one join on the detail page, which is cheap, and one
//   extra table per round C entity — which is zero, because the same
//   table serves every entity type.
//
// WHY TYPED COLUMNS AND NOT ONE NVARCHAR VALUE
//
//   Same reason. "10" < "9" as text. A number stored as text cannot be
//   range-filtered or summed without a CAST that fails on the first bad
//   row. Exactly one of the four typed columns is filled per row; which
//   one is decided by the definition's FieldType.
//
//   Dropdown values are stored in TextValue as the option's KEY, not its
//   label, so renaming "Gold" to "Gold tier" does not strand every
//   record that picked it. Definitions are live; the label is looked up
//   at display time.
//
// DATES ARE CALENDAR DATES
//
//   DateValue is a DateOnly mapped to SQL DATE. It is never converted
//   between timezones. A renewal date of 1 March is 1 March for a viewer
//   in Mumbai, Manila, Dubai and Bangkok alike — converting it as if it
//   were an instant would show 28 February to anybody west of UTC.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

namespace MerkaiTrial.Domain.Entities
{
    public class CustomFieldDefinition
    {
        public Guid Id { get; set; }

        /// <summary>Strictly tenant-owned. There are no Merkai default fields.</summary>
        public Guid TenantId { get; set; }

        /// <summary>
        /// The kind of record this field belongs on — "Contact" in round A.
        /// A string rather than an int enum so a value written today still
        /// reads back correctly after an entity type is added or retired.
        /// See CustomFieldEntityTypes in the Application project.
        /// </summary>
        public string EntityType { get; set; } = string.Empty;

        /// <summary>What the person sees: "Renewal date". Unique per tenant + entity type.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>text | number | date | dropdown | checkbox. Fixed once created.</summary>
        public string FieldType { get; set; } = string.Empty;

        /// <summary>Optional hint shown under the input.</summary>
        public string? HelpText { get; set; }

        /// <summary>Required on the Create/Edit forms. Ignored for checkboxes.</summary>
        public bool IsRequired { get; set; }

        /// <summary>
        /// Off = retired. Hidden from forms; values already recorded stay
        /// and still show on the record, marked as retired.
        /// </summary>
        public bool IsActive { get; set; } = true;

        public int SortOrder { get; set; }

        /// <summary>
        /// 076. Shown as a column on the record's list page. Capped
        /// (CustomFieldLimits.MaxListColumns) because a contacts table with
        /// twelve extra columns is unreadable on a laptop. Filtering does
        /// NOT depend on this — every active field can be filtered on.
        /// </summary>
        public bool ShowInList { get; set; }

        /// <summary>
        /// 079. LEAD fields only: when the lead is converted, its value is
        /// copied into THIS Contact field on the contact the conversion
        /// creates (or fills it in on an existing contact that has none).
        /// Same field type only; dropdowns are matched by choice NAME,
        /// because the two fields' choice keys are different.
        ///
        /// Not a foreign key — deleting the Contact field clears this
        /// instead of being blocked by it (DeleteCustomFieldHandler).
        /// Null on every non-Lead field.
        /// </summary>
        public Guid? MapToContactFieldId { get; set; }

        /// <summary>079. LEAD fields only: the Deal field that receives this value on conversion.</summary>
        public Guid? MapToDealFieldId { get; set; }

        /// <summary>Number fields only: 0–4 decimal places accepted and shown.</summary>
        public int DecimalPlaces { get; set; }

        /// <summary>
        /// Dropdown fields only: [{ "key": "o_1a2b3c4d", "label": "Gold", "isActive": true }, ...].
        /// Keys are generated by the server and never change.
        /// </summary>
        public string? OptionsJson { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public bool IsDeleted { get; set; }
    }

    public class CustomFieldValue
    {
        public Guid Id { get; set; }

        /// <summary>Strictly tenant-owned, same as the record it belongs to.</summary>
        public Guid TenantId { get; set; }

        public Guid DefinitionId { get; set; }

        /// <summary>
        /// The Contact (or, from round C, Company ...) this value belongs to.
        /// Not a foreign key: one column serves every entity type. The
        /// definition's EntityType says which table this id points into.
        /// </summary>
        public Guid EntityId { get; set; }

        /// <summary>text, and dropdown (the option KEY).</summary>
        public string? TextValue { get; set; }

        /// <summary>number. DECIMAL(18,4).</summary>
        public decimal? NumberValue { get; set; }

        /// <summary>date. SQL DATE — a calendar date, never timezone-converted.</summary>
        public DateOnly? DateValue { get; set; }

        /// <summary>checkbox. Only ever true: an unticked box has no row.</summary>
        public bool? BoolValue { get; set; }

        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }

        public CustomFieldDefinition? Definition { get; set; }
    }
}
