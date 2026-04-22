# EF Schema Drift Checker - Conception

## Objectif

Créer un outil CLI distribué sous forme de Dotnet Tool permettant de comparer :

* Le modèle EF Core défini dans un assembly
* Le schéma réel d'une base de données

L'objectif est de détecter les incohérences entre les entités EF Core et la structure réelle de la base.

Exemples d'incohérences :

* Table absente en base
* Table présente en base mais absente du modèle
* Colonne absente
* Colonne supplémentaire
* Type SQL différent
* Nullabilité différente
* Clé primaire différente
* Index différent
* Taille de colonne différente
* Schéma SQL différent
* Nom de table ou de colonne personnalisé non respecté

---

# Stack technique

* .NET 10
* Entity Framework Core
* Spectre.Console
* System.CommandLine
* Microsoft.Extensions.DependencyInjection
* Microsoft.Extensions.Hosting
* Microsoft.Extensions.Configuration
* Microsoft.EntityFrameworkCore.Relational
* Microsoft.EntityFrameworkCore.Design
* Microsoft.Data.SqlClient

---

# Nom proposé

`ef-schema-diff`

Commande d'installation :

```bash
dotnet tool install --global ef-schema-diff
```

Exemple d'utilisation :

```bash
ef-schema-diff \
  --assembly ./MyProject.Infrastructure.dll \
  --dbcontext MyAppDbContext \
  --connection-string "Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True" \
  --provider sqlserver
```

---

# Commandes

## compare

Commande principale :

```bash
ef-schema-diff compare [options]
```

### Options

```bash
--assembly <path>
```

Chemin vers l'assembly contenant le DbContext.

```bash
--startup-assembly <path>
```

Optionnel. Permet de charger les dépendances et la configuration si le DbContext dépend d'un projet startup différent.

```bash
--dbcontext <name>
```

Nom complet ou simple du DbContext à utiliser.

```bash
--connection-string <value>
```

Chaîne de connexion vers la base cible.

```bash
--provider <sqlserver|postgresql|mysql|sqlite>
```

Provider de base de données.

```bash
--schema <name>
```

Optionnel. Permet de filtrer sur un schéma spécifique.

```bash
--ignore-table <pattern>
```

Option répétable. Ignore certaines tables.

Exemples :

```bash
--ignore-table "__EFMigrationsHistory"
--ignore-table "Hangfire.*"
--ignore-table "sysdiagrams"
```

```bash
--ignore-column <pattern>
```

Option répétable. Ignore certaines colonnes.

Exemples :

```bash
--ignore-column "*.CreatedAt"
--ignore-column "*.UpdatedAt"
--ignore-column "Audit_*"
```

```bash
--exclude-navigation-tables
```

Ignore les tables de jointure automatiques many-to-many.

```bash
--output <table|json|markdown|sarif>
```

Format de sortie.

```bash
--output-file <path>
```

Écrit le résultat dans un fichier.

```bash
--fail-on-diff
```

Retourne un code de sortie non nul si des différences sont détectées.

```bash
--verbose
```

Affiche les détails techniques.

```bash
--no-color
```

Désactive les couleurs Spectre.Console.

---

# Codes de retour

```text
0 = Aucun écart détecté
1 = Écarts détectés
2 = Erreur de configuration ou d'arguments
3 = Erreur de connexion à la base
4 = Impossible de charger le DbContext
5 = Exception inattendue
```

---

# Types de différences

## TableMissingInDatabase

Une table existe dans EF Core mais pas en base.

Exemple :

```text
Users
```

## TableMissingInModel

Une table existe en base mais pas dans EF Core.

## ColumnMissingInDatabase

Une colonne existe dans EF Core mais pas en base.

Exemple :

```text
Users.Email
```

## ColumnMissingInModel

Une colonne existe en base mais pas dans EF Core.

## ColumnTypeMismatch

Exemple :

```text
Users.Email
EF      : nvarchar(200)
Database: nvarchar(100)
```

## NullabilityMismatch

Exemple :

```text
Users.LastLoginAt
EF      : nullable
Database: not null
```

## MaxLengthMismatch

## PrecisionMismatch

## ScaleMismatch

## PrimaryKeyMismatch

## ForeignKeyMismatch

## IndexMissingInDatabase

## IndexMissingInModel

## UniqueConstraintMismatch

## SchemaMismatch

---

# Architecture proposée

## Projet

```text
src/
 ├── EfSchemaDiff.Cli/
 ├── EfSchemaDiff.Core/
 ├── EfSchemaDiff.Infrastructure/
 └── EfSchemaDiff.Tests/
```

## Responsabilités

### EfSchemaDiff.Cli

* Parsing des arguments avec System.CommandLine
* Affichage Spectre.Console
* Gestion des codes de sortie
* Sérialisation des résultats

### EfSchemaDiff.Core

Contient les modèles métier :

* DatabaseSchema
* TableDefinition
* ColumnDefinition
* IndexDefinition
* ForeignKeyDefinition
* SchemaDiffResult
* SchemaDifference
* DiffSeverity
* DiffType

### EfSchemaDiff.Infrastructure

Contient :

* Extraction du modèle EF Core
* Lecture du schéma SQL réel
* Comparaison
* Providers SQL Server / PostgreSQL / MySQL / SQLite

---

# Extraction du modèle EF Core

Utiliser :

```csharp
dbContext.Model.GetEntityTypes()
```

Pour chaque entité :

* Schéma
* Nom de table
* Colonnes
* Type SQL
* Nullabilité
* Longueur max
* Précision
* Scale
* Clé primaire
* Clés étrangères
* Index

Exemple :

```csharp
var entityTypes = dbContext.Model.GetEntityTypes();

foreach (var entityType in entityTypes)
{
    var tableName = entityType.GetTableName();
    var schema = entityType.GetSchema();

    foreach (var property in entityType.GetProperties())
    {
        var storeObject = StoreObjectIdentifier.Table(tableName!, schema);

        var column = new ColumnDefinition
        {
            Name = property.GetColumnName(storeObject),
            StoreType = property.GetColumnType(),
            IsNullable = property.IsNullable,
            MaxLength = property.GetMaxLength(),
            Precision = property.GetPrecision(),
            Scale = property.GetScale()
        };
    }
}
```

---

# Lecture du schéma réel

Créer une abstraction :

```csharp
public interface IDatabaseSchemaReader
{
    Task<DatabaseSchema> ReadAsync(
        string connectionString,
        CancellationToken cancellationToken);
}
```

Implémentations :

* SqlServerSchemaReader
* PostgreSqlSchemaReader
* MySqlSchemaReader
* SqliteSchemaReader

Pour SQL Server, utiliser :

* INFORMATION_SCHEMA.TABLES
* INFORMATION_SCHEMA.COLUMNS
* INFORMATION_SCHEMA.KEY_COLUMN_USAGE
* INFORMATION_SCHEMA.TABLE_CONSTRAINTS
* sys.indexes
* sys.index_columns
* sys.columns
* sys.foreign_keys
* sys.foreign_key_columns

---

# Comparaison

Créer un composant :

```csharp
public interface ISchemaComparer
{
    SchemaDiffResult Compare(
        DatabaseSchema efSchema,
        DatabaseSchema databaseSchema,
        SchemaCompareOptions options);
}
```

La comparaison doit être :

* Case-insensitive par défaut
* Tolérante aux différences de casse de provider
* Capable de normaliser certains types SQL équivalents

Exemples :

```text
nvarchar(max) == nvarchar
int == integer
decimal(18,2) == numeric(18,2)
bit == boolean
datetime2 == timestamp
```

Créer un composant dédié :

```csharp
public interface IStoreTypeNormalizer
{
    string Normalize(string? storeType);
}
```

---

# Sortie console Spectre.Console

Exemple si aucune différence :

```text
✔ No schema differences found
```

Exemple avec différences :

```text
┌───────────────────────────────┬──────────────────────────┬──────────────────────┐
│ Type                          │ Object                   │ Details              │
├───────────────────────────────┼──────────────────────────┼──────────────────────┤
│ ColumnMissingInDatabase       │ Users.Email             │ Missing in database  │
│ ColumnTypeMismatch            │ Users.Name              │ nvarchar(100) != 50  │
│ NullabilityMismatch           │ Users.LastLoginAt       │ EF nullable          │
└───────────────────────────────┴──────────────────────────┴──────────────────────┘
```

Afficher également un résumé :

```text
Differences found: 12
- Errors: 10
- Warnings: 2
```

---

# Sortie JSON

Exemple :

```json
{
  "hasDifferences": true,
  "differenceCount": 2,
  "differences": [
    {
      "type": "ColumnMissingInDatabase",
      "objectName": "Users.Email",
      "details": "Column exists in EF but not in database"
    }
  ]
}
```

---

# SARIF

Prévoir un export SARIF afin de permettre une intégration avec :

* GitHub Code Scanning
* Azure DevOps
* SonarQube
* Pipelines CI/CD

---

# Chargement du DbContext

L'outil doit supporter plusieurs stratégies :

1. Recherche d'un type implémentant `IDesignTimeDbContextFactory<TContext>`
2. Recherche d'un constructeur prenant `DbContextOptions<TContext>`
3. Chargement du startup assembly et exécution du HostBuilder
4. Fallback avec création manuelle via réflexion

---

# Cas particuliers à gérer

* Entités sans table
* Owned types
* TPH / TPT / TPC
* Tables de jointure many-to-many automatiques
* Colonnes shadow
* Colonnes calculées
* Colonnes rowversion / timestamp
* Computed columns
* Temporal tables
* Séquences
* Default values
* Triggers
* Vues SQL mappées
* Entités keyless
* Schémas multiples
* Colonnes renommées
* Colonnes JSON
* Index filtrés

---

# Tests

Prévoir :

* Tests unitaires sur le comparateur
* Tests d'intégration avec SQL Server en conteneur Docker
* Jeux de données de test avec écarts connus
* Snapshots JSON de résultats attendus

Exemple :

```bash
docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=Your_password123" \
  -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

---

# Évolutions futures

* Génération automatique d'un script SQL correctif
* Mode interactif
* Support des procédures stockées
* Support des vues matérialisées
* Intégration GitHub Action
* Intégration Azure DevOps task
* Export HTML
* Export Markdown enrichi
* Comparaison entre deux bases sans EF
