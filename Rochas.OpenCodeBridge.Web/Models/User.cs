using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Rochas.Data.Specification.Annotations;

namespace Rochas.OpenCodeBridge.Web.Models;

// Usuario do console (login + CRUD). Comentarios pt-BR, identificadores en-US.
[Table("users")]
public sealed class User
{
    [Key]
    public int? Id { get; set; }

    [Filterable]
    [Column("name")]
    public string Name { get; set; } = "";

    [Filterable]
    [Column("email")]
    public string Email { get; set; } = "";

    [Column("password_hash")]
    public string PasswordHash { get; set; } = "";

    [Column("active")]
    public bool Active { get; set; }

    [Column("is_admin")]
    public bool IsAdmin { get; set; } = false;
}
