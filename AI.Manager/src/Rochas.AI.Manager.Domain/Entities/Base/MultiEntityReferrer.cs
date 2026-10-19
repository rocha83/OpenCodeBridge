using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;
using Rochas.AI.Manager.Domain.Helpers;

namespace Rochas.AI.Manager.Domain.Entities.Base;

public abstract class MultiEntityReferrer : BaseEntity
{
    [Column("entity_hash_code")]
    public uint EntityHashCode { get; set; }

    protected MultiEntityReferrer() { }

    protected MultiEntityReferrer(uint entityHashCode)
    {
        EntityHashCode = entityHashCode;
    }
}
