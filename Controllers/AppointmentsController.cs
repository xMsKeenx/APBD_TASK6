using APBD_TASK6.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace APBD_TASK6.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AppointmentsController : ControllerBase
    {
        private readonly string _connectionString;

        public AppointmentsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string not found.");
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<AppointmentListDto>>> GetAppointments(
            [FromQuery] string? status,
            [FromQuery] string? patientLastName)
        {
            var appointments = new List<AppointmentListDto>();

            await using var connection = new SqlConnection(_connectionString);

            var query = @"
                SELECT
                    a.IdAppointment,
                    a.AppointmentDate,
                    a.Status,
                    a.Reason,
                    p.FirstName + N' ' + p.LastName AS PatientFullName,
                    p.Email AS PatientEmail
                FROM dbo.Appointments a
                JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                WHERE (@Status IS NULL OR a.Status = @Status)
                  AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
                ORDER BY a.AppointmentDate;";

            await using var command = new SqlCommand(query, connection);

            command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            command.Parameters.AddWithValue("@PatientLastName", (object?)patientLastName ?? DBNull.Value);

            await connection.OpenAsync();

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                appointments.Add(new AppointmentListDto
                {
                    IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                    AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                    Status = reader.GetString(reader.GetOrdinal("Status")),
                    Reason = reader.GetString(reader.GetOrdinal("Reason")),
                    PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                    PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail"))
                });
            }

            return Ok(appointments);
        }

        [HttpGet("{idAppointment}")]
        public async Task<ActionResult<AppointmentDetailsDto>> GetAppointmentById([FromRoute] int idAppointment)
        {
            await using var connection = new SqlConnection(_connectionString);

            var query = @"
                SELECT
                    a.IdAppointment,
                    a.AppointmentDate,
                    a.Status,
                    a.Reason,
                    a.InternalNotes,
                    a.CreatedAt,
                    p.FirstName + N' ' + p.LastName AS PatientFullName,
                    p.Email AS PatientEmail,
                    p.PhoneNumber AS PatientPhoneNumber,
                    d.FirstName + N' ' + d.LastName AS DoctorFullName,
                    d.LicenseNumber AS DoctorLicenseNumber
                FROM dbo.Appointments a
                JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
                JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
                WHERE a.IdAppointment = @IdAppointment;";

            await using var command = new SqlCommand(query, connection);

            command.Parameters.AddWithValue("@IdAppointment", idAppointment);

            await connection.OpenAsync();

            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return NotFound(new ErrorResponseDto { Message = $"Appointment with ID {idAppointment} not found." });
            }

            var appointmentDetails = new AppointmentDetailsDto
            {
                IdAppointment = reader.GetInt32(reader.GetOrdinal("IdAppointment")),
                AppointmentDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Reason = reader.GetString(reader.GetOrdinal("Reason")),
                InternalNotes = reader.IsDBNull(reader.GetOrdinal("InternalNotes"))
                    ? string.Empty
                    : reader.GetString(reader.GetOrdinal("InternalNotes")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                PatientFullName = reader.GetString(reader.GetOrdinal("PatientFullName")),
                PatientEmail = reader.GetString(reader.GetOrdinal("PatientEmail")),
                PatientPhoneNumber = reader.GetString(reader.GetOrdinal("PatientPhoneNumber")),
                DoctorFullName = reader.GetString(reader.GetOrdinal("DoctorFullName")),
                DoctorLicenseNumber = reader.GetString(reader.GetOrdinal("DoctorLicenseNumber"))
            };

            return Ok(appointmentDetails);
        }

        [HttpPost]
        public async Task<ActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
        {
            if (request.AppointmentDate < DateTime.UtcNow)
            {
                return BadRequest(new ErrorResponseDto { Message = "Appointment date cannot be in the past." });
            }

            if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            {
                return BadRequest(new ErrorResponseDto { Message = "Reason must not be empty and cannot exceed 250 characters." });
            }

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var patientQuery = "SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient;";
            await using var patientCommand = new SqlCommand(patientQuery, connection);
            patientCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            var patientActiveObj = await patientCommand.ExecuteScalarAsync();

            if (patientActiveObj == null)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Patient with ID {request.IdPatient} does not exist." });
            }
            if (!(bool)patientActiveObj)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Patient with ID {request.IdPatient} is not active." });
            }

            var doctorQuery = "SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor;";
            await using var doctorCommand = new SqlCommand(doctorQuery, connection);
            doctorCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            var doctorActiveObj = await doctorCommand.ExecuteScalarAsync();

            if (doctorActiveObj == null)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Doctor with ID {request.IdDoctor} does not exist." });
            }
            if (!(bool)doctorActiveObj)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Doctor with ID {request.IdDoctor} is not active." });
            }

            var conflictQuery = @"
                SELECT COUNT(1) 
                FROM dbo.Appointments 
                WHERE IdDoctor = @IdDoctor 
                  AND AppointmentDate = @AppointmentDate
                  AND Status != N'Cancelled';";

            await using var conflictCommand = new SqlCommand(conflictQuery, connection);
            conflictCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            conflictCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);

            var conflictCount = (int)(await conflictCommand.ExecuteScalarAsync())!;
            if (conflictCount > 0)
            {
                return Conflict(new ErrorResponseDto { Message = "The doctor already has an appointment scheduled at this time." });
            }

            var insertQuery = @"
                INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, InternalNotes)
                OUTPUT INSERTED.IdAppointment
                VALUES (@IdPatient, @IdDoctor, @AppointmentDate, N'Scheduled', @Reason, NULL);";

            await using var insertCommand = new SqlCommand(insertQuery, connection);
            insertCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            insertCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            insertCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
            insertCommand.Parameters.AddWithValue("@Reason", request.Reason);

            var newId = (int)(await insertCommand.ExecuteScalarAsync())!;

            return CreatedAtAction(nameof(GetAppointmentById), new { idAppointment = newId }, new { IdAppointment = newId });
        }

        [HttpPut("{idAppointment}")]
        public async Task<ActionResult> UpdateAppointment([FromRoute] int idAppointment, [FromBody] UpdateAppointmentRequestDto request)
        {
            var validStatuses = new HashSet<string> { "Scheduled", "Completed", "Cancelled" };
            if (!validStatuses.Contains(request.Status))
            {
                return BadRequest(new ErrorResponseDto { Message = "Status must be Scheduled, Completed, or Cancelled." });
            }

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var getExistingQuery = "SELECT AppointmentDate, Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
            await using var getExistingCommand = new SqlCommand(getExistingQuery, connection);
            getExistingCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);

            await using var reader = await getExistingCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return NotFound(new ErrorResponseDto { Message = $"Appointment with ID {idAppointment} not found." });
            }

            var existingDate = reader.GetDateTime(reader.GetOrdinal("AppointmentDate"));
            var existingStatus = reader.GetString(reader.GetOrdinal("Status"));
            await reader.DisposeAsync();

            if (existingStatus == "Completed" && existingDate != request.AppointmentDate)
            {
                return BadRequest(new ErrorResponseDto { Message = "Cannot change the date of an already completed appointment." });
            }

            var patientQuery = "SELECT IsActive FROM dbo.Patients WHERE IdPatient = @IdPatient;";
            await using var patientCommand = new SqlCommand(patientQuery, connection);
            patientCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            var patientActiveObj = await patientCommand.ExecuteScalarAsync();

            if (patientActiveObj == null)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Patient with ID {request.IdPatient} does not exist." });
            }
            if (!(bool)patientActiveObj)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Patient with ID {request.IdPatient} is not active." });
            }

            var doctorQuery = "SELECT IsActive FROM dbo.Doctors WHERE IdDoctor = @IdDoctor;";
            await using var doctorCommand = new SqlCommand(doctorQuery, connection);
            doctorCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            var doctorActiveObj = await doctorCommand.ExecuteScalarAsync();

            if (doctorActiveObj == null)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Doctor with ID {request.IdDoctor} does not exist." });
            }
            if (!(bool)doctorActiveObj)
            {
                return BadRequest(new ErrorResponseDto { Message = $"Doctor with ID {request.IdDoctor} is not active." });
            }

            var conflictQuery = @"
                SELECT COUNT(1) 
                FROM dbo.Appointments 
                WHERE IdDoctor = @IdDoctor 
                  AND AppointmentDate = @AppointmentDate
                  AND Status != N'Cancelled'
                  AND IdAppointment != @IdAppointment;";

            await using var conflictCommand = new SqlCommand(conflictQuery, connection);
            conflictCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            conflictCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
            conflictCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);

            var conflictCount = (int)(await conflictCommand.ExecuteScalarAsync())!;
            if (conflictCount > 0)
            {
                return Conflict(new ErrorResponseDto { Message = "The doctor already has another appointment scheduled at this time." });
            }

            var updateQuery = @"
                UPDATE dbo.Appointments 
                SET IdPatient = @IdPatient, 
                    IdDoctor = @IdDoctor, 
                    AppointmentDate = @AppointmentDate, 
                    Status = @Status, 
                    Reason = @Reason, 
                    InternalNotes = @InternalNotes
                WHERE IdAppointment = @IdAppointment;";

            await using var updateCommand = new SqlCommand(updateQuery, connection);
            updateCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);
            updateCommand.Parameters.AddWithValue("@IdPatient", request.IdPatient);
            updateCommand.Parameters.AddWithValue("@IdDoctor", request.IdDoctor);
            updateCommand.Parameters.AddWithValue("@AppointmentDate", request.AppointmentDate);
            updateCommand.Parameters.AddWithValue("@Status", request.Status);
            updateCommand.Parameters.AddWithValue("@Reason", request.Reason);
            updateCommand.Parameters.AddWithValue("@InternalNotes", string.IsNullOrWhiteSpace(request.InternalNotes) ? DBNull.Value : request.InternalNotes);

            await updateCommand.ExecuteNonQueryAsync();

            return Ok(request);
        }

        [HttpDelete("{idAppointment}")]
        public async Task<ActionResult> DeleteAppointment([FromRoute] int idAppointment)
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var checkQuery = "SELECT Status FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
            await using var checkCommand = new SqlCommand(checkQuery, connection);
            checkCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);

            var statusObj = await checkCommand.ExecuteScalarAsync();

            if (statusObj == null)
            {
                return NotFound(new ErrorResponseDto { Message = $"Appointment with ID {idAppointment} not found." });
            }

            var status = (string)statusObj;
            if (status == "Completed")
            {
                return Conflict(new ErrorResponseDto { Message = "Cannot delete an appointment that is already completed." });
            }

            var deleteQuery = "DELETE FROM dbo.Appointments WHERE IdAppointment = @IdAppointment;";
            await using var deleteCommand = new SqlCommand(deleteQuery, connection);
            deleteCommand.Parameters.AddWithValue("@IdAppointment", idAppointment);

            await deleteCommand.ExecuteNonQueryAsync();

            return NoContent();
        }
    }
}